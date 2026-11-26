using Blam.Halo2;
using Blam.Halo2.TagData;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// A sprite of a particle bitmap: its texture, its extent with the longer side 1 (Halo's sprite aspect) and its registration point as a fraction of its size from the top left.
sealed class Halo2Sprite
{
	public Texture Texture;
	public Vector2 Extent = Vector2.One;
	public Vector2 Origin = new( 0.5f, 0.5f );

	// The texture's bindless index, looked up once per drawn frame.
	public int DrawFrame = int.MinValue;
	public int Index = -1;
}

// A particle bitmap's sequences of sprites; a sequence of a bitmap without sprites holds its whole bitmaps.
sealed class Halo2SpriteSheet
{
	public Halo2Sprite[][] Sequences = [];

	public int SequenceCount => Sequences.Length;

	static readonly Dictionary<(string Map, uint Tag), Halo2SpriteSheet> Cache = new();

	public static Halo2SpriteSheet Get( TagEntry bitmap )
	{
		if ( bitmap is null || bitmap.Address == 0 || bitmap.Class != BitmapTag.Class )
			return null;

		var key = (bitmap.Cache.FilePath, bitmap.Id);
		lock ( Cache )
		{
			if ( Cache.TryGetValue( key, out var cached ) )
				return cached;
		}

		var tag = new BitmapTag( bitmap );
		var pages = new Dictionary<int, (int Width, int Height, byte[] Pixels)?>();
		(int Width, int Height, byte[] Pixels)? Page( int index )
		{
			if ( index < 0 || index >= tag.Bitmaps.Count )
				return null;
			if ( !pages.TryGetValue( index, out var page ) )
				pages[index] = page = Halo2ParticleSpriteLoader.Decode( tag.Bitmaps[index] );
			return page;
		}

		Halo2Sprite Whole( int index )
		{
			if ( Page( index ) is not { } page )
				return null;

			var longer = MathF.Max( page.Width, page.Height );
			return new Halo2Sprite { Texture = Halo2ParticleSpriteLoader.Create( page.Width, page.Height, page.Pixels ), Extent = new Vector2( page.Width / longer, page.Height / longer ) };
		}

		var sequences = new List<Halo2Sprite[]>();
		foreach ( var sequence in tag.Sequences )
		{
			var sprites = new List<Halo2Sprite>();
			if ( sequence.Sprites.Count > 0 )
			{
				foreach ( var sprite in sequence.Sprites )
				{
					if ( Page( sprite.BitmapIndex ) is not { } page )
						continue;

					var (texture, origin) = Halo2ParticleSpriteLoader.Cut( page, sprite );
					var width = (sprite.Right - sprite.Left) * page.Width;
					var height = (sprite.Bottom - sprite.Top) * page.Height;
					var longer = MathF.Max( MathF.Max( width, height ), 1e-6f );
					sprites.Add( new Halo2Sprite { Texture = texture, Origin = origin, Extent = new Vector2( MathF.Abs( width ) / longer, MathF.Abs( height ) / longer ) } );
				}
			}
			else
			{
				for ( var i = sequence.FirstBitmapIndex; i < sequence.FirstBitmapIndex + Math.Max( sequence.BitmapCount, 1 ); i++ )
				{
					if ( Whole( i ) is { } sprite )
						sprites.Add( sprite );
				}
			}

			sequences.Add( sprites.ToArray() );
		}

		// A bitmap without sequences draws its first bitmap whole.
		if ( sequences.Count == 0 && Whole( 0 ) is { } first )
			sequences.Add( [first] );

		var sheet = new Halo2SpriteSheet { Sequences = sequences.ToArray() };
		lock ( Cache )
			Cache[key] = sheet;
		return sheet;
	}

	public static void Clear()
	{
		lock ( Cache )
			Cache.Clear();
	}
}

// How a particle tag draws: its sprites and its material in halo1_sprite.shader.
sealed class Halo2Look
{
	public Halo2SpriteSheet Sheet;
	public Material Material;

	// halo1_sprite.shader's blend mode (0 alpha blend, 7 alpha-multiply add).
	public int BlendMode;

	public bool Adds => BlendMode is 3 or 7;
}

// A particle tag (prt3), ready to make particles of.
sealed class Halo2ParticleDefinition
{
	public ParticleTag Tag;
	public Halo2Look Look;
	public TagEntry CollisionEffect;
	public TagEntry DeathEffect;

	// Flags as Halo 2 reads them, which differ from the tag's comments from bit 6 up (checked by which particles set them).
	public int Flags => Tag.Flags;
	public bool OneShot => (Flags & 0x8) != 0;
	public bool RandomSequence => (Flags & 0x10) != 0;
	public bool NoFrameBlending => (Flags & 0x20) != 0;
	public bool DiesAtRest => (Flags & 0x200) != 0;
	public bool DiesOnStructureCollision => (Flags & 0x400) != 0;
	public bool AuthoredVertically => (Flags & 0x2000) != 0;
}

// A particle movement tag (pmov): physics (gravity scale, drag, angular drag) and a collider (friction, elasticity), each a property of the particle.
sealed class Halo2Movement
{
	public ParticleProperty Gravity;
	public ParticleProperty Drag;
	public ParticleProperty AngularDrag;
	public ParticleProperty Friction;
	public ParticleProperty Elasticity;
	public bool Physics;
	public bool Collides;

	public static readonly Halo2Movement None = new();
}

// An emitter of a particle system, with what its per-frame properties depend on.
sealed class Halo2EmitterDefinition
{
	public EffectEmitter Emitter;
	public Halo2Movement Movement;

	// The emitter's direction (yaw, pitch) as a rotation of the location's frame.
	public Rotation Direction;
	public Vector3 Offset;

	// Whether size, tint and alpha are the same for every particle of the emitter, so they're evaluated once per frame.
	public bool SharedSize;
	public bool SharedTint;
	public bool SharedAlpha;
}

// A particle system of an effect event or attached to a particle.
sealed class Halo2SystemDefinition
{
	public EffectParticleSystem System;
	public Halo2ParticleDefinition Particle;
	public Halo2EmitterDefinition[] Emitters;

	public bool Local => System.CoordinateSystem != 0;
	public bool LoopingParticle => (System.Flags & 0x4) != 0;
	public bool InheritsVelocity => (System.Flags & 0x10) != 0;
	public bool SpreadBetweenTicks => (System.Flags & 0x80) != 0;
	public bool Persistent => (System.Flags & 0x100) != 0;
}

// A sound an effect event plays at a location when its duration starts.
sealed class Halo2EffectSound
{
	public int Location;
	public string Event;

	// The sound tag's path with its class, as games name tags ("sound/.../x.snd!").
	public string Tag;
}

// A decal an effect event leaves from a location when its duration starts (Halo2ParticleWorld.Decals).
sealed class Halo2EffectDecal
{
	public EffectPart Part;
	public DecalTag Decal;
}

sealed class Halo2EventDefinition
{
	public EffectEvent Event;
	public Halo2SystemDefinition[] Systems;
	public Halo2EffectSound[] Sounds;
	public Halo2EffectDecal[] Decals;
}

// An effect tag (effe).
sealed class Halo2EffectDefinition
{
	public string Path;
	public EffectTag Tag;
	public Halo2EventDefinition[] Events;

	// The longest a run of its events takes (seconds), and the longest its particles live.
	public float LongestRun;
	public float LongestLife;
}

// Halo 2's effects compiled on first use from the token their prefab roots carry (see Halo2ParticleWorld).
static class Halo2ParticleLibrary
{
	static readonly Dictionary<string, (string Map, uint Id)> Tokens = new();
	static readonly Dictionary<string, Halo2EffectDefinition> Effects = new();
	static readonly Dictionary<string, Halo2ParticleDefinition> Particles = new();
	static readonly Dictionary<string, Halo2Movement> Movements = new();

	// An effect tag's token: halo2_fx_ and 32 bits of its path's hash.
	public static string Token( string effectPath ) => $"halo2_fx_{Hash( effectPath ):x8}";

	public static bool IsToken( string tag ) => tag.Length == 17 && tag.StartsWith( "halo2_fx_", StringComparison.Ordinal );

	static uint Hash( string text )
	{
		var hash = 2166136261u;
		foreach ( var c in text.ToLowerInvariant() )
		{
			hash ^= c;
			hash *= 16777619u;
		}

		return hash;
	}

	// Makes an effect tag known by its token, unless a map already has.
	public static void Register( CacheFile cache, TagEntry tag )
	{
		if ( !EffectTag.IsEffect( tag ) || string.IsNullOrEmpty( tag.Path ) )
			return;

		lock ( Tokens )
			Tokens.TryAdd( Token( tag.Path ), (cache.FilePath, tag.Id) );
	}

	// The effect a token names, or null.
	public static Halo2EffectDefinition Get( string token )
	{
		(string Map, uint Id) source;
		lock ( Tokens )
		{
			if ( !Tokens.TryGetValue( token, out source ) )
				return null;
		}

		var tag = Halo2Mount.Current?.GetCache( source.Map )?.GetTag( source.Id );
		return Effect( tag );
	}

	// An effect tag, compiled once per path; null if it isn't one or can't be read.
	public static Halo2EffectDefinition Effect( TagEntry tag )
	{
		if ( !EffectTag.IsEffect( tag ) || string.IsNullOrEmpty( tag.Path ) )
			return null;

		lock ( Effects )
		{
			if ( Effects.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		Halo2EffectDefinition result = null;
		try
		{
			result = CompileEffect( tag );
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( e, $"Couldn't read the effect {tag.Path}: {e.Message}" );
		}

		lock ( Effects )
			Effects[tag.Path] = result;
		return result;
	}

	public static void Clear()
	{
		lock ( Effects ) Effects.Clear();
		lock ( Particles ) Particles.Clear();
		lock ( Movements ) Movements.Clear();
		lock ( Tokens ) Tokens.Clear();
		Halo2SpriteSheet.Clear();
	}

	static Halo2EffectDefinition CompileEffect( TagEntry tag )
	{
		var effect = new EffectTag( tag );
		var events = new List<Halo2EventDefinition>();
		var longestLife = 0f;
		foreach ( var ev in effect.Events )
		{
			var systems = new List<Halo2SystemDefinition>();
			foreach ( var system in ev.ParticleSystems )
			{
				if ( System( system ) is { } compiled )
				{
					systems.Add( compiled );
					foreach ( var emitter in system.Emitters )
						longestLife = MathF.Max( longestLife, Highest( emitter.Lifespan ) );
				}
			}

			var sounds = new List<Halo2EffectSound>();
			var decals = new List<Halo2EffectDecal>();
			foreach ( var part in ev.Parts )
			{
				if ( part.Type is { Address: not 0 } sound && sound.Class.ToString() == "snd!" && !string.IsNullOrEmpty( sound.Path ) && Halo2Effects.Exists( Halo2Sounds.EventPath( sound.Path ) ) )
					sounds.Add( new Halo2EffectSound { Location = part.Location, Event = Halo2Sounds.EventPath( sound.Path ), Tag = TagJsonWriter.TagName( sound ) } );
				else if ( part.Type is { Address: not 0 } decal && decal.Class == DecalTag.Class )
					decals.Add( new Halo2EffectDecal { Part = part, Decal = new DecalTag( decal ) } );
			}

			events.Add( new Halo2EventDefinition { Event = ev, Systems = systems.ToArray(), Sounds = sounds.ToArray(), Decals = decals.ToArray() } );
		}

		return new Halo2EffectDefinition
		{
			Path = tag.Path,
			Tag = effect,
			Events = events.ToArray(),
			LongestRun = effect.Events.Sum( e => MathF.Max( MathF.Max( e.Delay.X, e.Delay.Y ), 0 ) + MathF.Max( MathF.Max( e.Duration.X, e.Duration.Y ), 0 ) ),
			LongestLife = longestLife,
		};
	}

	// The most a property can be (its function's output range), for how long particles may live.
	static float Highest( ParticleProperty p ) => p.IsEmpty ? 1 : MathF.Max( p.Function.Minimum, p.Function.Maximum );

	// A particle system, or null if it isn't drawn (disabled, not rendered, or a particle the mount can't draw).
	public static Halo2SystemDefinition System( EffectParticleSystem system )
	{
		if ( (system.Flags & 0x28) != 0 || Particle( system.Particle ) is not { } particle )
			return null;

		var emitters = system.Emitters.Select( e => new Halo2EmitterDefinition
		{
			Emitter = e,
			Movement = Movement( e.Physics ),
			Offset = new Vector3( e.Offset.X, e.Offset.Y, e.Offset.Z ),
			Direction = Rotation.From( -e.Direction.Y.RadianToDegree(), e.Direction.X.RadianToDegree(), 0 ),
			SharedSize = !PerParticle( e.Size ),
			SharedTint = !PerParticle( e.Tint ),
			SharedAlpha = !PerParticle( e.Alpha ),
		} ).ToArray();

		return emitters.Length == 0 ? null : new Halo2SystemDefinition { System = system, Particle = particle, Emitters = emitters };
	}

	// Whether a property differs between an emitter's particles (a function of a particle input).
	static bool PerParticle( ParticleProperty p ) =>
		p.Uses( ParticleInput.ParticleAge ) || p.Uses( ParticleInput.ParticleEmitTime ) || p.Uses( ParticleInput.ParticleRandom1 ) || p.Uses( ParticleInput.ParticleRandom2 )
		|| p.Uses( ParticleInput.ParticleRandom3 ) || p.Uses( ParticleInput.ParticleRandom4 ) || p.Uses( ParticleInput.ParticleRotation )
		|| p.Uses( ParticleInput.ExplosionAnimation ) || p.Uses( ParticleInput.ExplosionRotation );

	static Halo2ParticleDefinition Particle( TagEntry tag )
	{
		if ( !ParticleTag.IsParticle( tag ) || string.IsNullOrEmpty( tag.Path ) )
			return null;

		lock ( Particles )
		{
			if ( Particles.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		Halo2ParticleDefinition result = null;
		var particle = new ParticleTag( tag );
		var materialPath = Halo2Effects.MaterialPath( tag.Path );
		if ( Halo2Effects.Look( particle ) is { } look && Halo2Effects.Exists( materialPath ) && Halo2SpriteSheet.Get( particle.Bitmap ) is { SequenceCount: > 0 } sheet )
		{
			var material = Material.Load( materialPath );
			if ( material is not null && !material.IsError )
			{
				result = new Halo2ParticleDefinition
				{
					Tag = particle,
					Look = new Halo2Look { Sheet = sheet, Material = material, BlendMode = look.BlendMode },
					CollisionEffect = EffectTag.IsEffect( particle.CollisionEffect ) ? particle.CollisionEffect : null,
					DeathEffect = EffectTag.IsEffect( particle.DeathEffect ) ? particle.DeathEffect : null,
				};
			}
		}

		lock ( Particles )
			Particles[tag.Path] = result;
		return result;
	}

	static Halo2Movement Movement( TagEntry tag )
	{
		if ( tag is null || tag.Address == 0 || tag.Class != ParticleMovementTag.Class || string.IsNullOrEmpty( tag.Path ) )
			return Halo2Movement.None;

		lock ( Movements )
		{
			if ( Movements.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		var movement = new ParticleMovementTag( tag );
		var result = new Halo2Movement
		{
			Gravity = movement.Parameter( 0, 0 ),
			Drag = movement.Parameter( 0, 1 ),
			AngularDrag = movement.Parameter( 0, 2 ),
			Friction = movement.Parameter( 1, 0 ),
			Elasticity = movement.Parameter( 1, 1 ),
			Physics = movement.Movements.Any( m => m.Type == 0 ),
			Collides = movement.Collides && movement.Movements.Any( m => m.Type == 1 ),
		};

		lock ( Movements )
			Movements[tag.Path] = result;
		return result;
	}
}
