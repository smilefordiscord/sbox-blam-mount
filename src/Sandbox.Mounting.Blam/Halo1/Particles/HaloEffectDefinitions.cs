using Blam;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// A sprite's texture, size and registration point in Halo's units (texture extent x sheet width, for height too).
sealed class HaloSprite
{
	public Texture Texture;
	public Vector2 Size;

	// The texture's bindless index and whether it's usable, looked up once per drawn frame.
	public int DrawFrame = int.MinValue;
	public int Index = -1;

	// The registration point, from the sprite's top left corner, in the same units.
	public Vector2 Origin;
}

// A particle bitmap's sequences of sprites (HaloSprite). A sequence without sprites has none here: Halo draws nothing for it.
sealed class HaloSpriteSheet
{
	public HaloSprite[][] Sequences = [];

	public int SequenceCount => Sequences.Length;

	// The sprite of a sequence, or null if either is out of range.
	public HaloSprite Get( int sequence, int sprite ) =>
		sequence >= 0 && sequence < Sequences.Length && sprite >= 0 && sprite < Sequences[sequence].Length ? Sequences[sequence][sprite] : null;

	public int SpriteCount( int sequence ) => sequence >= 0 && sequence < Sequences.Length ? Sequences[sequence].Length : 0;

	static readonly Dictionary<(string Map, uint Tag), HaloSpriteSheet> Cache = new();

	public static HaloSpriteSheet Get( CacheFile cache, TagEntry bitmap )
	{
		if ( bitmap is null || bitmap.Class != TagClasses.Bitmap )
			return null;

		var key = (cache.FilePath, bitmap.Id);
		lock ( Cache )
		{
			if ( Cache.TryGetValue( key, out var cached ) )
				return cached;
		}

		var tag = new BitmapTag( cache, bitmap );
		var sheet = new HaloSpriteSheet { Sequences = new HaloSprite[tag.Sequences.Count][] };
		for ( var s = 0; s < tag.Sequences.Count; s++ )
		{
			var sequence = tag.Sequences[s];
			if ( sequence.Sprites.Count == 0 )
			{
				sheet.Sequences[s] = [];
				continue;
			}

			// The textures line up with the sprites unless a sheet couldn't be read.
			var textures = SpriteTextures.Sequence( cache, bitmap, s );
			var sprites = new HaloSprite[sequence.Sprites.Count];
			for ( var i = 0; i < sprites.Length; i++ )
			{
				var sprite = sequence.Sprites[i];
				var page = sprite.BitmapIndex >= 0 && sprite.BitmapIndex < tag.Bitmaps.Count ? tag.Bitmaps[sprite.BitmapIndex] : null;
				var width = page?.Width ?? 1;
				sprites[i] = new HaloSprite
				{
					Texture = textures.Count == sprites.Length ? textures[i] : null,
					Size = new Vector2( (sprite.Right - sprite.Left) * width, (sprite.Bottom - sprite.Top) * width ),
					Origin = new Vector2( sprite.RegistrationX * width, sprite.RegistrationY * width ),
				};
			}

			sheet.Sequences[s] = sprites;
		}

		lock ( Cache )
			Cache[key] = sheet;
		return sheet;
	}
}

// A point physics tag (pphy) as Halo moves points with it (see HaloParticleWorld's point physics).
sealed class HaloPhysics
{
	public uint Flags;
	public float MassOverRadiusCubed;
	public float WaterBuoyancy;
	public float AirBuoyancy;
	public float Density;
	public float AirFriction;
	public float WaterFriction;
	public float ContactFriction;
	public float Elasticity;

	public bool CollidesWithStructures => (Flags & 0x2) != 0;
	public bool CollidesWithWater => (Flags & 0x4) != 0;
	public bool SimpleWind => (Flags & 0x8) != 0;
	public bool DampedWind => (Flags & 0x10) != 0;
	public bool NoGravity => (Flags & 0x20) != 0;

	// What a point without physics moves by: nothing pulls or slows it.
	public static readonly HaloPhysics None = new() { Flags = 0x20 };

	public HaloPhysics() { }

	public HaloPhysics( PointPhysicsTag tag )
	{
		Flags = tag.Flags;
		MassOverRadiusCubed = tag.MassOverRadiusCubed;
		WaterBuoyancy = tag.WaterGravityScale;
		AirBuoyancy = tag.AirGravityScale;
		Density = tag.Density;
		AirFriction = tag.AirFriction;
		WaterFriction = tag.WaterFriction;
		ContactFriction = tag.SurfaceFriction;
		Elasticity = tag.Elasticity;
	}

	// Halo's blend of two physics (its flags are the first's): (1 - t) of a and t of b.
	public static HaloPhysics Lerp( HaloPhysics a, HaloPhysics b, float t, HaloPhysics into )
	{
		var t0 = 1 - t;
		into.Flags = a.Flags;
		into.MassOverRadiusCubed = t0 * a.MassOverRadiusCubed + t * b.MassOverRadiusCubed;
		into.WaterBuoyancy = t0 * a.WaterBuoyancy + t * b.WaterBuoyancy;
		into.AirBuoyancy = t0 * a.AirBuoyancy + t * b.AirBuoyancy;
		into.Density = t0 * a.Density + t * b.Density;
		into.AirFriction = t0 * a.AirFriction + t * b.AirFriction;
		into.WaterFriction = t0 * a.WaterFriction + t * b.WaterFriction;
		into.ContactFriction = t0 * a.ContactFriction + t * b.ContactFriction;
		into.Elasticity = t0 * a.Elasticity + t * b.Elasticity;
		return into;
	}
}

// What a particle or particle system state draws with: its sprites and material.
sealed class HaloLook
{
	public HaloSpriteSheet Sheet;
	public Material Material;
	public FramebufferBlendFunction Blend;
	public bool NonlinearTint;
	public int FadeMode;

	// Halo takes the color's alpha only for alpha blended sprites or a nonlinear tint; the others' opacity is their fade alone.
	public bool UsesColorAlpha => Blend == FramebufferBlendFunction.AlphaBlend || NonlinearTint;

	// How far toward the viewer a zsprite's depth map reaches, in radii; 0 for a plain sprite.
	public float ZSpriteScale;

	// The order Halo draws particle tags in (their tag index).
	public int Order;
}

// A particle tag (part), ready to make particles of.
sealed class HaloParticleDefinition
{
	public string Path;
	public ParticleTag Tag;
	public HaloLook Look;
	public HaloPhysics Physics;

	// What plays where a particle hits the level, and where it dies: an effect prefab or a sound, or neither.
	public string CollisionEffect;
	public string DeathEffect;
	public HaloContactSound CollisionSound;
	public HaloContactSound DeathSound;
}

// A sound a particle plays on contact, with what Halo checks before playing an impulse sound.
sealed class HaloContactSound
{
	public string Event;

	// World units. Farther from the listener, it doesn't play.
	public float MaximumDistance;
	public float SkipFraction;
	public Vector2 SkipModifiers;
	public Vector2 GainModifiers;
	public Vector2 PitchModifiers;

	// Copies of it that may play at once, 0 for no limit, and how long one plays before a new one may take its place.
	public int Copies;
	public float PreemptionSeconds;

	// Played instead once more than PromotionCount copies have played within as many of its longest permutation.
	public HaloContactSound Promotion;
	public int PromotionCount;
	public float LongestSeconds;
}

// A particle system state of a particle system type (the particles' look and motion as they age).
sealed class HaloSystemParticleState
{
	public ParticleState State;
	public HaloLook Look;
	public HaloPhysics Physics;
}

// One type of a particle system (pctl).
sealed class HaloSystemType
{
	public ParticleSystemType Type;
	public HaloSystemParticleState[] ParticleStates;

	// Each type state's variables in Halo's order: scale, animation, rotation, alpha, red, green, blue, radius, minimum count, creation rate.
	public float[][] StateVariables;
	public float[] Constants;

	public uint Flags => Type.Flags;
	public bool Disabled => (Flags & 0x100) != 0;
}

// A particle system tag (pctl).
sealed class HaloSystemDefinition
{
	public string Path;
	public HaloSystemType[] Types;

	// What moves an unattached system itself, or null.
	public HaloPhysics SystemPhysics;
}

// A light an effect makes: it fades over its duration by its falloff function.
sealed class HaloEffectLight
{
	public LightTag Light;
	public Model Flare;
}

// A part of an effect event the runtime makes: a particle system or a light. Sounds, decals, objects and damage are the prefab's and the game's.
sealed class HaloEffectPart
{
	public EffectPart Part;
	public HaloSystemDefinition System;
	public HaloEffectLight Light;
}

// A block of particles of an effect event.
sealed class HaloEffectParticles
{
	public EffectParticle Block;
	public HaloParticleDefinition Particle;

	// The block's direction, normalized (x forward in the location's space).
	public Vector3 Direction;
}

sealed class HaloEffectEvent
{
	public EffectEvent Event;
	public HaloEffectPart[] Parts;
	public HaloEffectParticles[] Particles;
}

// An effect tag (effe).
sealed class HaloEffectDefinition
{
	public string Path;
	public EffectTag Tag;
	public HaloEffectEvent[] Events;
}

// Halo CE's effects, particle systems and particles, compiled on first use from the token their prefabs carry.
static class HaloEffectLibrary
{
	static readonly Dictionary<string, (string Map, uint Id)> Tokens = new();
	static readonly Dictionary<string, object> Compiled = new();
	static readonly Dictionary<string, HaloParticleDefinition> Particles = new();
	static readonly Dictionary<string, HaloContactSound> ContactSounds = new();
	static readonly Dictionary<string, HaloSystemDefinition> Systems = new();
	static readonly Dictionary<string, HaloLook> Looks = new();
	static int _order;

	// The token tag of an effect or particle system tag: halo_fx_ and 32 bits of its path's hash.
	public static string Token( TagEntry tag ) => $"halo_fx_{Hash( $"{tag.Class}:{tag.Path}" ):x8}";

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

	// Makes an effect or particle system tag known by its token, unless a map has already.
	public static void Register( CacheFile cache, TagEntry tag )
	{
		if ( tag is null || string.IsNullOrEmpty( tag.Path ) )
			return;

		lock ( Tokens )
			Tokens.TryAdd( Token( tag ), (cache.FilePath, tag.Id) );
	}

	public static bool IsToken( string tag ) => tag.StartsWith( "halo_fx_", StringComparison.Ordinal ) && tag.Length == 16;

	// The effect or particle system definition a token names, or null.
	public static object Get( string token )
	{
		lock ( Compiled )
		{
			if ( Compiled.TryGetValue( token, out var cached ) )
				return cached;
		}

		(string Map, uint Id) source;
		lock ( Tokens )
		{
			if ( !Tokens.TryGetValue( token, out source ) )
				return null;
		}

		object result = null;
		try
		{
			var cache = HaloCeMount.Current?.GetCache( source.Map );
			var tag = cache?.GetTag( source.Id );
			if ( tag is not null )
				result = tag.Class == EffectTagClasses.Effect ? CompileEffect( cache, tag ) : tag.Class == EffectTagClasses.ParticleSystem ? System( cache, tag ) : null;
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't read the effect {token}: {e.Message}" );
		}

		lock ( Compiled )
			Compiled[token] = result;
		return result;
	}

	// Forgets everything compiled (the mount is going away).
	public static void Clear()
	{
		lock ( Compiled ) Compiled.Clear();
		lock ( Particles ) Particles.Clear();
		lock ( Systems ) Systems.Clear();
		lock ( Looks ) Looks.Clear();
		lock ( Tokens ) Tokens.Clear();
	}

	static HaloEffectDefinition CompileEffect( CacheFile cache, TagEntry tag )
	{
		var effect = new EffectTag( cache, tag );
		var events = new List<HaloEffectEvent>();
		foreach ( var ev in effect.Events )
		{
			var parts = new List<HaloEffectPart>();
			foreach ( var part in ev.Parts )
			{
				if ( part.Type.Class == EffectTagClasses.ParticleSystem )
				{
					if ( System( cache, part.Type ) is { } system )
						parts.Add( new HaloEffectPart { Part = part, System = system } );
				}
				else if ( part.Type.Class == EffectTagClasses.Light )
				{
					var light = new LightTag( cache, part.Type );
					parts.Add( new HaloEffectPart { Part = part, Light = new HaloEffectLight { Light = light, Flare = EffectBuilder.LoadLensFlare( cache, part.Type ) } } );
				}
			}

			var particles = new List<HaloEffectParticles>();
			foreach ( var block in ev.Particles )
			{
				if ( block.Particle.Class != EffectTagClasses.Particle || Particle( cache, block.Particle ) is not { } particle )
					continue;

				var direction = new Vector3( block.Direction.X, block.Direction.Y, block.Direction.Z );
				particles.Add( new HaloEffectParticles { Block = block, Particle = particle, Direction = direction.LengthSquared > 1e-12f ? direction.Normal : Vector3.Forward } );
			}

			events.Add( new HaloEffectEvent { Event = ev, Parts = parts.ToArray(), Particles = particles.ToArray() } );
		}

		return new HaloEffectDefinition { Path = tag.Path, Tag = effect, Events = events.ToArray() };
	}

	// A particle tag, compiled once per path.
	public static HaloParticleDefinition Particle( CacheFile cache, TagEntry tag )
	{
		lock ( Particles )
		{
			if ( Particles.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		HaloParticleDefinition result = null;
		var part = new ParticleTag( cache, tag );
		var look = Look( cache, part.Bitmap, ParticleSpriteMaterial.FullPath( tag.Path ), part.BlendFunction, part.NonlinearTint, part.FadeMode, part.ZSprite, part.ZSpriteRadiusScale );
		if ( look is not null )
		{
			var collision = ContactEffect( cache, part.CollisionEffect );
			var death = ContactEffect( cache, part.DeathEffect );
			result = new HaloParticleDefinition
			{
				Path = tag.Path,
				Tag = part,
				Look = look,
				Physics = Physics( cache, part.Physics ),
				CollisionEffect = collision.Effect,
				CollisionSound = collision.Sound,
				DeathEffect = death.Effect,
				DeathSound = death.Sound,
			};
		}

		lock ( Particles )
			Particles[tag.Path] = result;
		return result;
	}

	// A particle system tag, compiled once per path.
	public static HaloSystemDefinition System( CacheFile cache, TagEntry tag )
	{
		lock ( Systems )
		{
			if ( Systems.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		var system = new ParticleSystemTag( cache, tag );
		var types = new List<HaloSystemType>();
		for ( var t = 0; t < system.Types.Count; t++ )
		{
			var type = system.Types[t];
			var states = new HaloSystemParticleState[type.ParticleStates.Count];
			for ( var s = 0; s < states.Length; s++ )
			{
				var state = type.ParticleStates[s];
				states[s] = new HaloSystemParticleState
				{
					State = state,
					Look = Look( cache, state.Bitmap, ParticleSpriteMaterial.FullPath( tag.Path, t, s ), state.BlendFunction, state.NonlinearTint, state.FadeMode, state.ZSprite, state.ZSpriteRadiusScale ),
					Physics = Physics( cache, state.Physics ),
				};
			}

			types.Add( new HaloSystemType
			{
				Type = type,
				ParticleStates = states,
				Constants = type.Constants.ToArray(),
				StateVariables = type.States.Select( s => new[]
				{
					s.ScaleMultiplier, s.AnimationRateMultiplier, s.RotationRateMultiplier,
					s.ColorMultiplier.W, s.ColorMultiplier.X, s.ColorMultiplier.Y, s.ColorMultiplier.Z,
					s.RadiusMultiplier, s.MinimumCount, s.CreationRate,
				} ).ToArray(),
			} );
		}

		var result = new HaloSystemDefinition
		{
			Path = tag.Path,
			Types = types.ToArray(),
			SystemPhysics = system.SystemPhysics is null ? null : Physics( cache, system.SystemPhysics ),
		};

		lock ( Systems )
			Systems[tag.Path] = result;
		return result;
	}

	static HaloPhysics Physics( CacheFile cache, TagEntry tag ) =>
		tag is null || tag.Class != PointPhysicsClass ? HaloPhysics.None : new HaloPhysics( new PointPhysicsTag( cache, tag ) );

	static readonly TagClass PointPhysicsClass = new( "pphy" );

	static HaloLook Look( CacheFile cache, TagEntry bitmap, string materialPath, FramebufferBlendFunction blend, bool nonlinear, int fadeMode, bool zsprite, float zspriteRadiusScale )
	{
		if ( bitmap is null )
			return null;

		lock ( Looks )
		{
			if ( Looks.TryGetValue( materialPath, out var cached ) )
				return cached;
		}

		HaloLook look = null;
		var sheet = HaloSpriteSheet.Get( cache, bitmap );
		var material = Sandbox.Mounting.Directory.Get( EffectPrefabs.Ident )?.GetByPath( materialPath ) is null ? null : Material.Load( materialPath );
		if ( sheet is not null && material is not null && !material.IsError )
		{
			look = new HaloLook
			{
				Sheet = sheet,
				Material = material,
				Blend = blend,
				NonlinearTint = nonlinear,
				FadeMode = fadeMode,
				// A zsprite whose radius scale is 0 reaches one radius.
				ZSpriteScale = !zsprite ? 0 : zspriteRadiusScale > 0 ? zspriteRadiusScale : 1,
				Order = global::System.Threading.Interlocked.Increment( ref _order ),
			};
		}

		lock ( Looks )
			Looks[materialPath] = look;
		return look;
	}

	// What a particle's collision or death effect plays: an effect's prefab or a sound's event, or null if it's neither or isn't registered.
	static (string Effect, HaloContactSound Sound) ContactEffect( CacheFile cache, TagEntry tag )
	{
		if ( tag is null || string.IsNullOrEmpty( tag.Path ) )
			return (null, null);

		if ( tag.Class == EffectPlan.SoundClass )
			return (null, ContactSound( cache, tag ));

		var path = $"mount://{EffectPrefabs.Ident}/{HaloCeMount.ResourcePath( tag.Path )}.prefab";
		return tag.Class == EffectTagClasses.Effect && Mounted( path ) ? (path, null) : (null, null);
	}

	static bool Mounted( string path ) => Sandbox.Mounting.Directory.Get( EffectPrefabs.Ident )?.GetByPath( path ) is not null;

	// A sound tag's contact sound, made once per path and shared, since its copy limit counts every particle's copies.
	static HaloContactSound ContactSound( CacheFile cache, TagEntry tag )
	{
		lock ( ContactSounds )
		{
			if ( ContactSounds.TryGetValue( tag.Path, out var cached ) )
				return cached;
		}

		var path = HaloSounds.EventPath( EffectPrefabs.Ident, tag.Path );
		if ( !Mounted( path ) )
		{
			lock ( ContactSounds )
				ContactSounds[tag.Path] = null;
			return null;
		}

		var sound = new SoundTag( cache, tag );
		var limits = sound.CopyLimits;
		var result = new HaloContactSound
		{
			Event = path,
			MaximumDistance = sound.Distances.Maximum,
			SkipFraction = sound.SkipFraction,
			SkipModifiers = new Vector2( sound.ZeroSkipFractionModifier, sound.OneSkipFractionModifier ),
			GainModifiers = new Vector2( sound.ZeroGainModifier, sound.OneGainModifier ),
			PitchModifiers = new Vector2( sound.ZeroPitchModifier, sound.OnePitchModifier ),
			Copies = limits.Copies,
			PreemptionSeconds = limits.PreemptionMilliseconds / 1000f,
			PromotionCount = sound.PromotionCount,
			LongestSeconds = sound.LongestPermutationLength / 1000f,
		};

		// Cached before its promotion is made, so a promotion that leads back here ends.
		lock ( ContactSounds )
			ContactSounds[tag.Path] = result;
		if ( sound.PromotionCount > 0 && sound.PromotionSound is { } promotion && promotion.Class == EffectPlan.SoundClass )
			result.Promotion = ContactSound( cache, promotion );
		return result;
	}
}
