using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// What a particle property's function reads (the tags' input variable enum; 21-24 are four more particle randoms MCC's compute shader reads from the particle's second random word).
public enum ParticleInput : byte
{
	ParticleAge = 0,
	SystemAge = 1,
	ParticleRandom = 2,
	SystemRandom = 3,
	ParticleCorrelation1 = 4,
	ParticleCorrelation2 = 5,
	ParticleCorrelation3 = 6,
	ParticleCorrelation4 = 7,
	SystemCorrelation1 = 8,
	SystemCorrelation2 = 9,
	ParticleEmissionTime = 10,
	LocationLod = 11,
	GameTime = 12,
	EffectAScale = 13,
	EffectBScale = 14,
	ParticleRotation = 15,
	LocationRandom = 16,
	DistanceFromEmitter = 17,
	ExplosionAnimation = 18,
	ExplosionRotation = 19,
	Invalid = 20,
	ParticleCorrelation5 = 21,
	ParticleCorrelation6 = 22,
	ParticleCorrelation7 = 23,
	ParticleCorrelation8 = 24,
}

// A property of an emitter, particle or movement (0x20 bytes): u8 input, u8 range input, u8 output modifier (1 plus, 2 times), u8 modifier input, the function's data reference, a runtime constant and flags.
public sealed record ParticleProperty( ParticleInput Input, ParticleInput Range, int Modifier, ParticleInput ModifierInput, Halo3Function Function )
{
	public const int Size = 0x20;

	public static readonly ParticleProperty None = new( 0, 0, 0, 0, Halo3Function.Empty );

	public static ParticleProperty Read( TagView view, int field ) =>
		new( (ParticleInput)view.U8( field ), (ParticleInput)view.U8( field + 1 ), view.U8( field + 2 ), (ParticleInput)view.U8( field + 3 ), new Halo3Function( view.Data( field + 4 ).ToArray() ) );

	public bool IsEmpty => Function.IsEmpty;

	// The property for the inputs (indexed by ParticleInput), or empty when it has no function.
	public float Evaluate( ReadOnlySpan<float> inputs, float empty = 0 )
	{
		if ( Function.IsEmpty )
			return empty;

		var value = Function.Evaluate( Get( inputs, Input ), Get( inputs, Range ) );
		return Modifier switch
		{
			1 => value + Get( inputs, ModifierInput ),
			2 => value * Get( inputs, ModifierInput ),
			_ => value,
		};
	}

	// A colour property's colour (0-1); white when empty or scalar.
	public Vector3 EvaluateColor( ReadOnlySpan<float> inputs )
	{
		if ( Function.IsEmpty || !Function.IsColor )
			return Vector3.One;

		return Halo3Function.ToVector( Function.ColorAt( Function.Curve( Get( inputs, Input ), Get( inputs, Range ) ) ) );
	}

	public bool Uses( ParticleInput input ) => !Function.IsEmpty &&
		(Input == input || (Function.IsRange && Range == input) || (Modifier != 0 && ModifierInput == input));

	static float Get( ReadOnlySpan<float> inputs, ParticleInput input ) => (int)input < inputs.Length ? inputs[(int)input] : 0;

	public override string ToString() => Function.IsEmpty ? "-" : $"{Function}({Input}{(Function.IsRange ? $", {Range}" : "")}){(Modifier == 1 ? $" + {ModifierInput}" : Modifier == 2 ? $" x {ModifierInput}" : "")}";
}

public enum EmissionShape : byte
{
	Sprayer = 0,
	Disc = 1,
	Globe = 2,
	Implode = 3,
	Tube = 4,
	Halo = 5,
	ImpactContour = 6,
	ImpactArea = 7,
	Debris = 8,
	Line = 9,
	BreakableSurface = 10,
}

// A particle movement (pmov, or an emitter's own copy): type 0 physics (parameters 0 gravity scale, 1 air friction, 2 rotational friction), 1 collider (0 friction, 1 elasticity), 2 swarm, 3 wind.
public sealed record ParticleMovement( int Type, IReadOnlyDictionary<int, ParticleProperty> Parameters )
{
	public const int Size = 0x18;

	public static IReadOnlyList<ParticleMovement> ReadBlock( TagView view, int field ) =>
		view.Block( field, Size ).Select( m => new ParticleMovement( m.U16( 0 ),
			m.Block( 0x4, 0x24 ).GroupBy( p => p.I32( 0 ) ).ToDictionary( g => g.Key, g => ParticleProperty.Read( g.First(), 0x4 ) ) ) ).ToList();

	public ParticleProperty Parameter( int id ) => Parameters.GetValueOrDefault( id ) ?? ParticleProperty.None;
}

// An emitter of a particle system (0x2F0).
public sealed class EffectEmitter
{
	public const int Size = 0x2F0;

	public string Name { get; }
	public EmissionShape Shape { get; }
	public int Flags { get; }
	public ParticleProperty Offset { get; }
	public Vector3 OffsetFrom { get; }
	public Vector3 OffsetTo { get; }
	public ParticleProperty Direction { get; }

	// The relative direction's two ends: (yaw, pitch) radians.
	public Vector2 DirectionFrom { get; }
	public Vector2 DirectionTo { get; }
	public ParticleProperty EmissionRadius { get; }
	public ParticleProperty EmissionAngle { get; }
	public ParticleProperty EmissionAxisAngle { get; }
	public ParticleProperty StartingCount { get; }
	public ParticleProperty MaximumCount { get; }
	public ParticleProperty EmissionRate { get; }
	public ParticleProperty Lifespan { get; }
	public TagEntry MovementTemplate { get; }
	public int MovementFlags { get; }
	public IReadOnlyList<ParticleMovement> Movements { get; }
	public ParticleProperty SelfAcceleration { get; }
	public Vector3 AccelerationFrom { get; }
	public Vector3 AccelerationTo { get; }
	public ParticleProperty InitialVelocity { get; }
	public ParticleProperty Rotation { get; }
	public ParticleProperty RotationRate { get; }
	public ParticleProperty ParticleSize { get; }
	public ParticleProperty Scale { get; }
	public ParticleProperty Tint { get; }
	public ParticleProperty Alpha { get; }
	public ParticleProperty AlphaBlackPoint { get; }

	public EffectEmitter( TagView e )
	{
		Name = e.StringId( 0x0 ) ?? "";
		Shape = (EmissionShape)e.U8( 0x4 );
		Flags = e.U8( 0x5 );
		Offset = ParticleProperty.Read( e, 0x18 );
		OffsetFrom = e.Vec3( 0x38 );
		OffsetTo = e.Vec3( 0x44 );
		Direction = ParticleProperty.Read( e, 0x50 );
		DirectionFrom = e.Vec2( 0x74 );
		DirectionTo = e.Vec2( 0x80 );
		EmissionRadius = ParticleProperty.Read( e, 0x88 );
		EmissionAngle = ParticleProperty.Read( e, 0xA8 );
		EmissionAxisAngle = ParticleProperty.Read( e, 0xC8 );
		StartingCount = ParticleProperty.Read( e, 0xE8 );
		MaximumCount = ParticleProperty.Read( e, 0x108 );
		EmissionRate = ParticleProperty.Read( e, 0x128 );
		Lifespan = ParticleProperty.Read( e, 0x148 );
		MovementTemplate = e.Reference( 0x168 );
		MovementFlags = e.I32( 0x178 );
		Movements = ParticleMovement.ReadBlock( e, 0x17C );
		SelfAcceleration = ParticleProperty.Read( e, 0x188 );
		AccelerationFrom = e.Vec3( 0x1A8 );
		AccelerationTo = e.Vec3( 0x1B4 );
		InitialVelocity = ParticleProperty.Read( e, 0x1C0 );
		Rotation = ParticleProperty.Read( e, 0x1E0 );
		RotationRate = ParticleProperty.Read( e, 0x200 );
		ParticleSize = ParticleProperty.Read( e, 0x220 );
		Scale = ParticleProperty.Read( e, 0x240 );
		Tint = ParticleProperty.Read( e, 0x260 );
		Alpha = ParticleProperty.Read( e, 0x280 );
		AlphaBlackPoint = ParticleProperty.Read( e, 0x2A0 );
	}

	// Movement flags: 0 physics, 1 collide with structure, 2 media, 3 scenery, 4 vehicles, 5 bipeds, 6 swarm, 7 wind.
	public bool HasPhysics => (MovementFlags & 0x1) != 0;
	public bool CollidesWithStructure => (MovementFlags & 0x2) != 0;

	public ParticleProperty MovementParameter( int type, int id ) =>
		Movements.Where( m => m.Type == type ).Select( m => m.Parameters.GetValueOrDefault( id ) ).FirstOrDefault( p => p is not null ) ?? ParticleProperty.None;

	public IEnumerable<ParticleProperty> Properties =>
	[
		Offset, Direction, EmissionRadius, EmissionAngle, EmissionAxisAngle, StartingCount, MaximumCount, EmissionRate, Lifespan,
		SelfAcceleration, InitialVelocity, Rotation, RotationRate, ParticleSize, Scale, Tint, Alpha, AlphaBlackPoint,
	];
}

// A particle system of an effect event (0x5C).
public sealed class EffectParticleSystem
{
	public const int Size = 0x5C;

	public TagEntry Particle { get; }
	public int Location { get; }

	// 0 world, 1 local.
	public int CoordinateSystem { get; }
	public int Environment { get; }
	public int Disposition { get; }

	// 0 any, 1 first person only, 2 third person only, 3 both.
	public int CameraMode { get; }
	public int SortBias { get; }
	public int Flags { get; }
	public float NearFadeRange { get; }
	public float NearFadeCutoff { get; }
	public float LodIn { get; }
	public float LodFeatherIn { get; }
	public float LodOut { get; }
	public float LodFeatherOut { get; }
	public IReadOnlyList<EffectEmitter> Emitters { get; }
	public float MaximumLifespan { get; }

	public EffectParticleSystem( TagView s )
	{
		Particle = s.Reference( 0x4 );
		Location = s.I32( 0x14 );
		CoordinateSystem = s.U16( 0x18 );
		Environment = s.U16( 0x1A );
		Disposition = s.U16( 0x1C );
		CameraMode = s.U16( 0x1E );
		SortBias = s.I16( 0x20 );
		Flags = s.U16( 0x22 );
		NearFadeRange = s.F32( 0x28 );
		NearFadeCutoff = s.F32( 0x2C );
		LodIn = s.F32( 0x34 );
		LodFeatherIn = s.F32( 0x38 );
		LodOut = s.F32( 0x40 );
		LodFeatherOut = s.F32( 0x44 );
		Emitters = s.Block( 0x4C, EffectEmitter.Size ).Select( e => new EffectEmitter( e ) ).ToList();
		MaximumLifespan = s.F32( 0x58 );
	}

	public bool IsLocal => CoordinateSystem != 0;

	// Flags: 2 LOD always 1, 5 disabled for debugging, 6 inherit effect velocity, 7 don't render, 12 particles die when the effect ends.
	public bool LodAlwaysOne => (Flags & 0x4) != 0;
	public bool Disabled => (Flags & 0x20) != 0;
	public bool InheritsVelocity => (Flags & 0x40) != 0;
	public bool NotRendered => (Flags & 0x80) != 0;
	public bool DieWithEffect => (Flags & 0x1000) != 0;

	// The system's LOD at a distance (world units): 0 outside its in and out distances, rising and falling over the feathers; 1 without an out distance.
	public float LodAt( float distance )
	{
		if ( LodAlwaysOne || LodOut <= 0 )
			return 1;
		if ( distance <= LodIn || distance >= LodOut )
			return 0;
		if ( LodFeatherIn > 0 && distance < LodIn + LodFeatherIn )
			return (distance - LodIn) / LodFeatherIn;
		if ( LodFeatherOut > 0 && distance > LodOut - LodFeatherOut )
			return (LodOut - distance) / LodFeatherOut;
		return 1;
	}
}

// A part of an effect event (0x60): a tag (sound, light, decal, object, damage, beam, contrail, light volume...) made at a location.
public sealed record EffectPart( int Environment, int Disposition, int Location, int SecondaryLocation, int Flags, int CameraMode, TagClass BaseGroup, TagEntry Type,
	Vector2 VelocityBounds, Vector2 VelocityOrientation, float VelocityConeAngle, Vector2 AngularVelocityBounds, Vector2 RadiusModifierBounds, Vector3 RelativeOffset, Vector2 RelativeOrientation )
{
	public const int Size = 0x60;

	public static EffectPart Read( TagView p ) => new( p.U16( 0x0 ), p.U16( 0x2 ), p.I16( 0x4 ), p.I16( 0x6 ), p.U16( 0x8 ), p.U8( 0xB ), p.Class( 0xC ), p.Reference( 0x10 ),
		p.Vec2( 0x20 ), p.Vec2( 0x28 ), p.F32( 0x30 ), p.Vec2( 0x34 ), p.Vec2( 0x3C ), p.Vec3( 0x44 ), p.Vec2( 0x50 ) );
}

// An acceleration of an effect event (0x14): pushes nearby objects.
public sealed record EffectAcceleration( int Location, float Acceleration, float InnerConeAngle, float OuterConeAngle );

// An effect event (0x44): delay, then duration, through which its particle systems emit.
public sealed class EffectEvent
{
	public const int Size = 0x44;

	public string Name { get; }
	public int Flags { get; }
	public float SkipFraction { get; }
	public Vector2 Delay { get; }
	public Vector2 Duration { get; }
	public IReadOnlyList<EffectPart> Parts { get; }
	public IReadOnlyList<EffectAcceleration> Accelerations { get; }
	public IReadOnlyList<EffectParticleSystem> ParticleSystems { get; }

	public EffectEvent( TagView e )
	{
		Name = e.StringId( 0x0 ) ?? "";
		Flags = e.I32( 0x4 );
		SkipFraction = e.F32( 0xC );
		Delay = e.Vec2( 0x10 );
		Duration = e.Vec2( 0x18 );
		Parts = e.Block( 0x20, EffectPart.Size ).Select( EffectPart.Read ).ToList();
		Accelerations = e.Block( 0x2C, 0x14 ).Select( a => new EffectAcceleration( a.I16( 0x4 ), a.F32( 0x8 ), a.F32( 0xC ), a.F32( 0x10 ) ) ).ToList();
		ParticleSystems = e.Block( 0x38, EffectParticleSystem.Size ).Select( s => new EffectParticleSystem( s ) ).ToList();
	}

	// Flags: 0 disabled for debugging, 1 particles die when the effect ends.
	public bool Disabled => (Flags & 0x1) != 0;
}

// An effect tag (effe, 0x68; Assembly's Halo3 effe.xml as documentation, checked against the data by BlamInspect h3 effects).
public sealed class EffectTag
{
	public static readonly TagClass Class = new( "effe" );

	public TagEntry Tag { get; }

	// Flags: 0 deleted when attachment deactivates, 1 run events in parallel, 4 parent position but world orientation, 6 cannot be restarted.
	public int Flags { get; }
	public int FixedRandomSeed { get; }
	public float RestartIfWithin { get; }
	public float ContinueIfWithin { get; }
	public float DeathDelay { get; }
	public int LoopStartEvent { get; }
	public IReadOnlyList<string> Locations { get; }
	public IReadOnlyList<EffectEvent> Events { get; }
	public TagEntry LoopingSound { get; }
	public int LoopingSoundLocation { get; }
	public int BindScaleToEvent { get; }
	public float AlwaysPlayDistance { get; }
	public float NeverPlayDistance { get; }

	public EffectTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		Flags = d.I32( 0x0 );
		FixedRandomSeed = d.I32( 0x4 );
		RestartIfWithin = d.F32( 0x8 );
		ContinueIfWithin = d.F32( 0xC );
		DeathDelay = d.F32( 0x10 );
		LoopStartEvent = d.I16( 0x18 );
		Locations = d.Block( 0x20, 0xC ).Select( l => l.StringId( 0x0 ) ?? "" ).ToList();
		Events = d.Block( 0x2C, EffectEvent.Size ).Select( e => new EffectEvent( e ) ).ToList();
		LoopingSound = d.Reference( 0x38 );
		LoopingSoundLocation = d.I8( 0x48 );
		BindScaleToEvent = d.I8( 0x49 );
		AlwaysPlayDistance = d.F32( 0x4C );
		NeverPlayDistance = d.F32( 0x50 );
	}

	public bool RunsEventsInParallel => (Flags & 0x2) != 0;
	public bool DeletedWhenAttachmentDeactivates => (Flags & 0x1) != 0;

	public string LocationName( int index ) => index >= 0 && index < Locations.Count ? Locations[index] : "";

	public static bool IsEffect( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;

	public override string ToString() => Tag.ToString();
}

// An effect, particle or sound a particle makes at its birth, collision or death (prt3 attachments, 0x14).
public sealed record ParticleAttachment( TagEntry Type, int Trigger, int Flags, ParticleInput PrimaryScale, ParticleInput SecondaryScale );

// A particle tag (prt3, 0x194).
public sealed class ParticleTag
{
	public static readonly TagClass Class = new( "prt3" );

	public TagEntry Tag { get; }

	// Main flags: 0 dies at rest, 1 dies on structure collision, 2 dies in media, 3 dies in air, 4 has sweetener.
	public int Flags { get; }
	public IReadOnlyList<ParticleAttachment> Attachments { get; }

	// Appearance flags: 0 random U mirror, 1 random V mirror, 2 random starting rotation, 3 tint from lightmap, 4 tint from diffuse texture, 5 bitmap authored vertically, 6 intensity affects alpha, 7 fade when viewed edge-on, 8 motion blur, 9 double sided.
	public int AppearanceFlags { get; }

	// 0 screen facing, 1 camera facing, 2 parallel to direction, 3 perpendicular to direction, 4 vertical, 5 horizontal, 6 local vertical, 7 local horizontal, 8 world, 9 velocity horizontal.
	public int BillboardStyle { get; }
	public int FirstSequence { get; }
	public int SequenceCount { get; }
	public Vector2 CenterOffset { get; }
	public float Curvature { get; }
	public float AngleFadeRange { get; }
	public float AngleFadeCutoff { get; }
	public ParticleProperty AspectRatio { get; }
	public ParticleProperty Color { get; }
	public ParticleProperty Intensity { get; }
	public ParticleProperty Alpha { get; }

	// Animation flags: 0 frame animation one shot, 1 can animate backwards.
	public int AnimationFlags { get; }
	public ParticleProperty FrameIndex { get; }
	public ParticleProperty AnimationRate { get; }
	public ParticleProperty PaletteAnimation { get; }
	public TagEntry Model { get; }

	// The sprite's rectangle in units of the particle's size (x, y, width, height; y up), as the runtime sprite block has it.
	public System.Numerics.Vector4 SpriteRectangle { get; }

	// The frames of the chosen sequences as texture rectangles (u, v, width, height), from the runtime frames block (its first element holds the count).
	public IReadOnlyList<System.Numerics.Vector4> Frames { get; }

	public ParticleTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		Flags = d.I32( 0x0 );
		Attachments = d.Block( 0x4, 0x14 ).Select( a => new ParticleAttachment( a.Reference( 0x0 ), a.U8( 0x10 ), a.U8( 0x11 ), (ParticleInput)a.U8( 0x12 ), (ParticleInput)a.U8( 0x13 ) ) ).ToList();
		AppearanceFlags = d.I32( 0x10 );
		BillboardStyle = d.U16( 0x14 );
		FirstSequence = d.I16( 0x16 );
		SequenceCount = d.I16( 0x18 );
		CenterOffset = d.Vec2( 0x1C );
		Curvature = d.F32( 0x24 );
		AngleFadeRange = d.F32( 0x28 );
		AngleFadeCutoff = d.F32( 0x2C );
		AspectRatio = ParticleProperty.Read( d, 0x7C );
		Color = ParticleProperty.Read( d, 0x9C );
		Intensity = ParticleProperty.Read( d, 0xBC );
		Alpha = ParticleProperty.Read( d, 0xDC );
		AnimationFlags = d.I32( 0xFC );
		FrameIndex = ParticleProperty.Read( d, 0x100 );
		AnimationRate = ParticleProperty.Read( d, 0x120 );
		PaletteAnimation = ParticleProperty.Read( d, 0x140 );
		Model = d.Reference( 0x160 );

		var sprite = d.Block( 0x17C, 0x10 );
		SpriteRectangle = sprite.Count > 0 ? sprite[0].Vec4( 0 ) : new System.Numerics.Vector4( -1, -1, 2, 2 );
		var frames = d.Block( 0x188, 0x10 );
		Frames = frames.Skip( 1 ).Select( f => f.Vec4( 0 ) ).ToList();
	}

	public bool DiesAtRest => (Flags & 0x1) != 0;
	public bool DiesOnStructureCollision => (Flags & 0x2) != 0;
	public bool RandomUMirror => (AppearanceFlags & 0x1) != 0;
	public bool RandomVMirror => (AppearanceFlags & 0x2) != 0;
	public bool RandomStartingRotation => (AppearanceFlags & 0x4) != 0;
	public bool AuthoredVertically => (AppearanceFlags & 0x20) != 0;
	public bool IntensityAffectsAlpha => (AppearanceFlags & 0x40) != 0;
	public bool FadesEdgeOn => (AppearanceFlags & 0x80) != 0;
	public bool OneShotAnimation => (AnimationFlags & 0x1) != 0;
	public bool IsModelParticle => Model is not null && Model.Address != 0;

	public static bool IsParticle( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;

	public override string ToString() => Tag.ToString();
}

// A particle movement tag (pmov, 0x20): its template, flags (as an emitter's movement flags) and movements.
public sealed class ParticleMovementTag
{
	public static readonly TagClass Class = new( "pmov" );

	public TagEntry Tag { get; }
	public TagEntry Template { get; }
	public int Flags { get; }
	public IReadOnlyList<ParticleMovement> Movements { get; }

	public ParticleMovementTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		Template = d.Reference( 0x0 );
		Flags = d.I32( 0x10 );
		Movements = ParticleMovement.ReadBlock( d, 0x14 );
	}
}

// An effect or other tag an object carries at a marker (obje attachments +0xBC, 0x20: type 0x0, marker 0x10, change colour 0x14, primary scale 0x18, secondary scale 0x1C).
public sealed record ObjectAttachment( TagEntry Type, string Marker, int ChangeColor, string PrimaryScale, string SecondaryScale )
{
	public static IReadOnlyList<ObjectAttachment> Read( TagEntry objectTag )
	{
		if ( !ObjectTag.IsObject( objectTag ) )
			return [];

		return objectTag.Data.Block( 0xBC, 0x20 )
			.Select( a => new ObjectAttachment( a.Reference( 0x0 ), a.StringId( 0x10 ) ?? "", a.U16( 0x14 ), a.StringId( 0x18 ) ?? "", a.StringId( 0x1C ) ?? "" ) )
			.Where( a => a.Type is not null && a.Type.Address != 0 )
			.ToList();
	}
}
