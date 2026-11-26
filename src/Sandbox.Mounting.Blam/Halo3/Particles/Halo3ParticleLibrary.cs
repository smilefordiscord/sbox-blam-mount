using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// A particle tag (prt3) ready to draw: its material in halo3_particle.shader, its frames and sprite rectangle, its flags.
sealed class Halo3ParticleDefinition
{
	public ParticleTag Tag;
	public Material Material;
	public bool Adds;

	// Texture rectangles (u, v, width, height) of its frames; one whole-texture frame when the tag has none.
	public Vector4[] Frames;

	// The runtime sprite rectangle in units of the particle's size (x, y, width, height; y up).
	public Vector4 SpriteRect;
	public bool FrameBlend;

	// Effects it makes at a collision and at its death (prt3 attachments with triggers 1 and 2).
	public TagEntry CollisionEffect;
	public TagEntry DeathEffect;

	// A distortion particle's look (specialized_rendering distortion or distortion_expensive): drawn into Halo3PostFx's offset target, not the scene.
	public Halo3DistortionLook Distortion;
}

// The base map (a signed offset map, by bindless index), depth fade range (s&box units), distortion_scale and whether it's the expensive kind (which keeps things in front from being pulled in).
sealed class Halo3DistortionLook
{
	public int Texture;
	public float DepthFadeRange;
	public float Scale;
	public bool Expensive;
}

// What an emitter's movement does: physics (gravity scale, air friction, rotational friction) and a collider (friction, elasticity), each a property of the particle.
sealed class Halo3Movement
{
	public ParticleProperty Gravity = ParticleProperty.None;
	public ParticleProperty Drag = ParticleProperty.None;
	public ParticleProperty AngularDrag = ParticleProperty.None;
	public ParticleProperty Friction = ParticleProperty.None;
	public ParticleProperty Elasticity = ParticleProperty.None;
	public bool Physics;
	public bool Collides;
}

sealed class Halo3EmitterDefinition
{
	public EffectEmitter Emitter;
	public Halo3Movement Movement;

	// Whether size, tint and alpha are the same for every particle of the emitter at a time.
	public bool SharedLook;
}

sealed class Halo3SystemDefinition
{
	public EffectParticleSystem System;
	public Halo3ParticleDefinition Particle;
	public Halo3EmitterDefinition[] Emitters;

	public bool Local => System.IsLocal;
}

// A sound an event plays at a location when its duration starts.
sealed class Halo3EffectSound
{
	public int Location;
	public string Event;
}

// An area screen effect an event starts at a location (Halo3ScreenEffects).
sealed class Halo3EffectScreenEffect
{
	public int Location;
	public ScreenEffectTag Tag;
}

// A light volume an event shows at a location through its duration.
sealed class Halo3LightVolumePart
{
	public int Location;
	public Halo3LightVolumeDefinition[] Volumes;

	// The part's relative offset (world units) and orientation at its location.
	public Vector3 Offset;
	public Rotation Orientation = Rotation.Identity;
}

// A light volume (ltvl) ready to draw: its row of profiles' functions and its material in halo3_particle.shader.
sealed class Halo3LightVolumeDefinition
{
	public LightVolume Volume;
	public Halo3ParticleDefinition Look;
}

sealed class Halo3EventDefinition
{
	public EffectEvent Event;
	public System.Numerics.Vector2 Delay;
	public System.Numerics.Vector2 Duration;
	public float SkipFraction;
	public int Flags;
	public Halo3SystemDefinition[] Systems;
	public Halo3EffectSound[] Sounds;
	public Halo3EffectScreenEffect[] ScreenEffects;
	public Halo3LightVolumePart[] LightVolumes;
}

sealed class Halo3EffectDefinition
{
	public string Path;
	public EffectTag Tag;
	public Halo3EventDefinition[] Events;
	public IReadOnlyList<string> Locations;
	public int LoopStartEvent;
	public bool Parallel;
	public bool DeletedWhenDeactivated;

	// The longest one run of its events takes (seconds), and the longest its particles live.
	public float LongestRun;
	public float LongestLife;
}

// Halo 3's effects, compiled on first use from the token their prefab roots carry (Halo3ParticleEffects).
static class Halo3ParticleLibrary
{
	public const string ShaderPath = "shaders/halo3_particle.shader";

	static readonly Dictionary<string, (string Map, uint Id)> Tokens = new();
	static readonly Dictionary<(string Map, string Path), Halo3EffectDefinition> Effects = new();
	static readonly Dictionary<(string Map, string Path), Halo3ParticleDefinition> Particles = new();

	// Each map compiles its own particles (materials, textures): only the most recently used maps' stay (as Halo3Materials keeps materials), live worlds hold what they use themselves.
	const int MapsKept = 2;
	static readonly List<string> UsedMaps = new();

	// Marks a map as just used; forgets what the maps past the last MapsKept compiled.
	static void Touch( string map )
	{
		string[] dropped;
		lock ( UsedMaps )
		{
			if ( UsedMaps.Count > 0 && UsedMaps[0] == map )
				return;

			UsedMaps.Remove( map );
			UsedMaps.Insert( 0, map );
			if ( UsedMaps.Count <= MapsKept )
				return;

			dropped = UsedMaps.Skip( MapsKept ).ToArray();
			UsedMaps.RemoveRange( MapsKept, UsedMaps.Count - MapsKept );
		}

		lock ( Effects )
			Forget( Effects, dropped );
		lock ( Particles )
			Forget( Particles, dropped );
		lock ( Volumes )
			Forget( Volumes, dropped );
	}

	static void Forget<T>( Dictionary<(string Map, string Path), T> cache, string[] maps )
	{
		foreach ( var key in cache.Keys.Where( k => maps.Contains( k.Map ) ).ToList() )
			cache.Remove( key );
	}

	// An effect tag's token: halo3_fx_ and 32 bits of its path's hash.
	public static string Token( string effectPath ) => $"halo3_fx_{Hash( effectPath ):x8}";

	public static bool IsToken( string tag ) => tag.Length == 17 && tag.StartsWith( "halo3_fx_", StringComparison.Ordinal );

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

	// Makes an effect known by its token, unless an earlier map has.
	public static void Register( CacheFile cache, TagEntry tag )
	{
		if ( !(EffectTag.IsEffect( tag ) || LightVolumeTag.IsLightVolume( tag )) || string.IsNullOrEmpty( tag.Path ) )
			return;

		lock ( Tokens )
			Tokens.TryAdd( Token( tag.Path ), (cache.FilePath, tag.Id) );
	}

	// The effect a token names: the given map's copy of it when that map has one (a level's effects take its exposure), else the registering map's.
	public static Halo3EffectDefinition Get( string token, string mapName = null )
	{
		(string Map, uint Id) source;
		lock ( Tokens )
		{
			if ( !Tokens.TryGetValue( token, out source ) )
				return null;
		}

		var registered = Halo3Mount.Current?.GetCache( source.Map )?.GetTag( source.Id );
		if ( registered is not null && mapName is not null && Halo3Mount.Current?.GetMap( mapName ) is { } map && map != registered.Cache
			&& map.FindTag( registered.Path, registered.Class.ToString() ) is { } own )
			return Effect( own );

		return Effect( registered );
	}

	// An effect tag compiled once per map and path (its materials take that map's exposure); null if it isn't one or can't be read.
	public static Halo3EffectDefinition Effect( TagEntry tag )
	{
		if ( !(EffectTag.IsEffect( tag ) || LightVolumeTag.IsLightVolume( tag )) || string.IsNullOrEmpty( tag.Path ) )
			return null;

		var key = (tag.Cache.FilePath, tag.Path);
		Touch( key.FilePath );
		lock ( Effects )
		{
			if ( Effects.TryGetValue( key, out var cached ) )
				return cached;
		}

		Halo3EffectDefinition result = null;
		try
		{
			result = EffectTag.IsEffect( tag ) ? Compile( tag ) : CompileLightVolumeEffect( tag );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't read the effect {tag.Path}: {e.Message}" );
		}

		lock ( Effects )
			Effects[key] = result;
		return result;
	}

	public static void Clear()
	{
		lock ( Effects ) Effects.Clear();
		lock ( Particles ) Particles.Clear();
		lock ( Volumes ) Volumes.Clear();
		lock ( Tokens ) Tokens.Clear();
		lock ( UsedMaps ) UsedMaps.Clear();
	}

	static Halo3EffectDefinition Compile( TagEntry tag )
	{
		var effect = new EffectTag( tag );
		var events = new List<Halo3EventDefinition>();
		var longestLife = 0f;
		foreach ( var ev in effect.Events )
		{
			var systems = new List<Halo3SystemDefinition>();
			if ( !ev.Disabled )
			{
				foreach ( var system in ev.ParticleSystems )
				{
					if ( CompileSystem( system ) is not { } compiled )
						continue;

					systems.Add( compiled );
					foreach ( var emitter in system.Emitters )
						longestLife = MathF.Max( longestLife, MathF.Max( emitter.Lifespan.Function.Highest, system.MaximumLifespan ) );
				}
			}

			var sounds = new List<Halo3EffectSound>();
			foreach ( var part in ev.Parts )
			{
				if ( part.Type is { Address: not 0 } sound && sound.Class.ToString() == "snd!" && Halo3Sounds.GetSound( sound.Path ) is not null )
					sounds.Add( new Halo3EffectSound { Location = part.Location, Event = Halo3Sounds.EventPath( sound.Path ) } );
			}

			var screenEffects = new List<Halo3EffectScreenEffect>();
			foreach ( var part in ev.Parts )
			{
				if ( !ev.Disabled && ScreenEffectTag.IsScreenEffect( part.Type ) )
					screenEffects.Add( new Halo3EffectScreenEffect { Location = part.Location, Tag = new ScreenEffectTag( part.Type ) } );
			}

			var volumes = new List<Halo3LightVolumePart>();
			foreach ( var part in ev.Parts )
			{
				if ( !ev.Disabled && part.CameraMode != 1 && LightVolumes( part.Type ) is { Length: > 0 } compiled )
					volumes.Add( new Halo3LightVolumePart
					{
						Location = part.Location,
						Volumes = compiled,
						Offset = new Vector3( part.RelativeOffset.X, part.RelativeOffset.Y, part.RelativeOffset.Z ),
						Orientation = Rotation.From( -part.RelativeOrientation.Y.RadianToDegree(), part.RelativeOrientation.X.RadianToDegree(), 0 ),
					} );
			}

			events.Add( new Halo3EventDefinition
			{
				Event = ev,
				Delay = ev.Delay,
				Duration = ev.Duration,
				SkipFraction = ev.SkipFraction,
				Flags = ev.Flags,
				Systems = systems.ToArray(),
				Sounds = sounds.ToArray(),
				ScreenEffects = screenEffects.ToArray(),
				LightVolumes = volumes.ToArray(),
			} );
		}

		return new Halo3EffectDefinition
		{
			Path = tag.Path,
			Tag = effect,
			Events = events.ToArray(),
			Locations = effect.Locations,
			LoopStartEvent = effect.LoopStartEvent,
			Parallel = effect.RunsEventsInParallel,
			DeletedWhenDeactivated = effect.DeletedWhenAttachmentDeactivates,
			LongestRun = effect.Events.Sum( e => MathF.Max( e.Delay.X, e.Delay.Y ) + MathF.Max( e.Duration.X, e.Duration.Y ) ),
			LongestLife = longestLife,
		};
	}

	// A light volume system an object carries by itself, as an effect of one event that shows it for ever at the root.
	static Halo3EffectDefinition CompileLightVolumeEffect( TagEntry tag )
	{
		if ( LightVolumes( tag ) is not { Length: > 0 } volumes )
			return null;

		var always = new System.Numerics.Vector2( 1e9f, 1e9f );
		return new Halo3EffectDefinition
		{
			Path = tag.Path,
			Events = [new Halo3EventDefinition { Duration = always, Systems = [], Sounds = [], LightVolumes = [new Halo3LightVolumePart { Location = 0, Volumes = volumes }] }],
			Locations = [""],
		};
	}

	// A particle system, or null if it isn't drawn (disabled, not rendered, first person only, or a particle the mount can't draw).
	static Halo3SystemDefinition CompileSystem( EffectParticleSystem system )
	{
		if ( system.Disabled || system.NotRendered || system.CameraMode == 1 || Particle( system.Particle ) is not { } particle )
			return null;

		var emitters = system.Emitters.Select( e => new Halo3EmitterDefinition
		{
			Emitter = e,
			Movement = Movement( e ),
			SharedLook = !PerParticle( e.ParticleSize ) && !PerParticle( e.Scale ) && !PerParticle( e.Tint ) && !PerParticle( e.Alpha ),
		} ).ToArray();

		return emitters.Length == 0 ? null : new Halo3SystemDefinition { System = system, Particle = particle, Emitters = emitters };
	}

	static readonly ParticleInput[] ParticleInputs =
	[
		ParticleInput.ParticleAge, ParticleInput.ParticleRandom, ParticleInput.ParticleCorrelation1, ParticleInput.ParticleCorrelation2,
		ParticleInput.ParticleCorrelation3, ParticleInput.ParticleCorrelation4, ParticleInput.ParticleEmissionTime, ParticleInput.ParticleRotation,
		ParticleInput.DistanceFromEmitter, ParticleInput.ParticleCorrelation5, ParticleInput.ParticleCorrelation6, ParticleInput.ParticleCorrelation7, ParticleInput.ParticleCorrelation8,
	];

	public static bool PerParticle( ParticleProperty p ) => ParticleInputs.Any( p.Uses );

	// The emitter's own copy of its movement (the template's, copied in by the tools).
	static Halo3Movement Movement( EffectEmitter e ) => new()
	{
		Gravity = e.MovementParameter( 0, 0 ),
		Drag = e.MovementParameter( 0, 1 ),
		AngularDrag = e.MovementParameter( 0, 2 ),
		Friction = e.MovementParameter( 1, 0 ),
		Elasticity = e.MovementParameter( 1, 1 ),
		Physics = e.HasPhysics && e.Movements.Any( m => m.Type == 0 ),
		Collides = e.CollidesWithStructure && e.Movements.Any( m => m.Type == 1 ),
	};

	// halo3_particle.shader's albedo modes, by shaders\particle's albedo option.
	static int AlbedoMode( string option ) => option switch
	{
		"diffuse_plus_billboard_alpha" => 1,
		"palettized" => 2,
		"palettized_plus_billboard_alpha" => 3,
		"diffuse_plus_sprite_alpha" => 4,
		"palettized_plus_sprite_alpha" => 5,
		_ => 0,
	};

	static readonly string[] BlendModes = ["opaque", "additive", "multiply", "alpha_blend", "double_multiply", "maximum", "multiply_add", "add_src_times_dstalpha", "add_src_times_srcalpha", "inv_alpha_blend", "pre_multiplied_alpha"];

	public static Halo3ParticleDefinition Particle( TagEntry tag )
	{
		if ( !ParticleTag.IsParticle( tag ) || string.IsNullOrEmpty( tag.Path ) )
			return null;

		var key = (tag.Cache.FilePath, tag.Path);
		Touch( key.FilePath );
		lock ( Particles )
		{
			if ( Particles.TryGetValue( key, out var cached ) )
				return cached;
		}

		Halo3ParticleDefinition result = null;
		try
		{
			result = CompileParticle( tag );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't read the particle {tag.Path}: {e.Message}" );
		}

		lock ( Particles )
			Particles[key] = result;
		return result;
	}

	static readonly Dictionary<(string Map, string Path), Halo3LightVolumeDefinition[]> Volumes = new();

	// A light volume system's volumes that can be drawn (a base map), compiled once per map and path.
	static Halo3LightVolumeDefinition[] LightVolumes( TagEntry tag )
	{
		if ( !LightVolumeTag.IsLightVolume( tag ) || string.IsNullOrEmpty( tag.Path ) )
			return null;

		var key = (tag.Cache.FilePath, tag.Path);
		Touch( key.FilePath );
		lock ( Volumes )
		{
			if ( Volumes.TryGetValue( key, out var cached ) )
				return cached;
		}

		var result = new List<Halo3LightVolumeDefinition>();
		try
		{
			foreach ( var volume in new LightVolumeTag( tag ).Volumes )
			{
				if ( volume.BaseMap is null )
					continue;

				var blend = Array.IndexOf( BlendModes, volume.Option( "blend_mode" ) ?? "additive" );
				var material = Material.Create( $"halo3 light volume {tag.Path} {volume.Name}", ShaderPath, anonymous: true );
				if ( material is null )
					continue;

				material.SetFeature( "F_BLEND_MODE", blend < 0 ? 1 : blend );
				material.Set( "g_flAlbedoMode", 0f );
				material.Set( "g_flExposureStops", Halo3Materials.CameraExposure( tag.Cache ) );
				var texture = Texture.Load( Halo3Mount.TexturePath( volume.BaseMap.Path, Math.Max( 0, volume.BaseMapIndex ) ) );
				if ( texture is null || texture.IsError )
					continue;
				material.Set( "g_tBaseMap", texture );
				material.Set( "g_flBaseMapSrgb", ShaderBitmaps.IsSrgb( volume.BaseMap, volume.BaseMapIndex ) ? 1f : 0f );

				result.Add( new Halo3LightVolumeDefinition
				{
					Volume = volume,
					Look = new Halo3ParticleDefinition { Material = material, Adds = blend is 1 or 5 or 6 or 7 or 8 or < 0, Frames = [new Vector4( 0, 0, 1, 1 )], SpriteRect = new Vector4( -0.5f, -0.5f, 1, 1 ) },
				} );
			}
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't read the light volume {tag.Path}: {e.Message}" );
		}

		var array = result.ToArray();
		lock ( Volumes )
			Volumes[key] = array;
		return array;
	}

	// specialized_rendering distortion (and _expensive; the _diffuse kinds aren't used by any map): the offset map and the template's distortion_scale and depth_fade_range.
	static Halo3ParticleDefinition CompileDistortion( ParticleTag particle, ShaderTag shader, string special )
	{
		if ( shader.Texture( "base_map" ) is not { Bitmap: not null } map )
			return null;
		var texture = Texture.Load( Halo3Mount.TexturePath( map.Bitmap.Path, Math.Max( 0, map.BitmapIndex ) ) );
		if ( texture is null || texture.IsError )
			return null;

		var frames = particle.Frames.Select( f => new Vector4( f.X, f.Y, f.Z, f.W ) ).ToArray();
		return new Halo3ParticleDefinition
		{
			Tag = particle,
			Frames = frames.Length > 0 ? frames : [new Vector4( 0, 0, 1, 1 )],
			SpriteRect = new Vector4( particle.SpriteRectangle.X, particle.SpriteRectangle.Y, particle.SpriteRectangle.Z, particle.SpriteRectangle.W ),
			Distortion = new Halo3DistortionLook
			{
				Texture = texture.Index,
				DepthFadeRange = shader.Scalar( "depth_fade_range", 0 ) * Halo3Mount.WorldScale,
				Scale = shader.Scalar( "distortion_scale", 1 ),
				Expensive = special.Contains( "expensive" ),
			},
		};
	}

	static Halo3ParticleDefinition CompileParticle( TagEntry tag )
	{
		var particle = new ParticleTag( tag );
		var shader = ShaderTag.Get( tag );

		// Mesh particles need their models.
		if ( shader?.Definition is null || particle.IsModelParticle )
			return null;
		if ( shader.Option( "specialized_rendering" ) is { } special && special != "none" )
			return special.StartsWith( "distortion" ) ? CompileDistortion( particle, shader, special ) : null;

		var blend = Array.IndexOf( BlendModes, shader.Option( "blend_mode" ) ?? "alpha_blend" );
		if ( blend < 0 )
			blend = 3;

		var material = Material.Create( $"halo3 particle {tag.Path}", ShaderPath, anonymous: true );
		if ( material is null )
			return null;

		material.SetFeature( "F_BLEND_MODE", blend );
		material.Set( "g_flAlbedoMode", (float)AlbedoMode( shader.Option( "albedo" ) ) );
		material.Set( "g_flFrameBlend", shader.Option( "frame_blend" ) == "on" ? 1f : 0f );
		material.Set( "g_flBlackPoint", shader.Option( "black_point" ) == "on" ? 1f : 0f );
		material.Set( "g_flDepthFadeRange", shader.Option( "depth_fade" ) == "on" ? shader.Scalar( "depth_fade_range", 0.25f ) * Halo3Mount.WorldScale : 0f );
		var selfIllum = shader.Option( "self_illumination" ) == "constant_color" ? shader.Vector( "self_illum_color", System.Numerics.Vector4.Zero ) : System.Numerics.Vector4.Zero;
		material.Set( "g_vSelfIllumColor", new Vector3( selfIllum.X, selfIllum.Y, selfIllum.Z ) * selfIllum.W );
		material.Set( "g_flExposureStops", Halo3Materials.CameraExposure( tag.Cache ) );
		material.Set( "g_flAltExposure", 1f );

		var hasBase = false;
		foreach ( var (name, slot) in new[] { ("base_map", "BaseMap"), ("palette", "Palette"), ("alpha_map", "AlphaMap") } )
		{
			if ( shader.Texture( name ) is not { Bitmap: not null } texture )
				continue;

			var loaded = Texture.Load( Halo3Mount.TexturePath( texture.Bitmap.Path, Math.Max( 0, texture.BitmapIndex ) ) );
			if ( loaded is null || loaded.IsError )
				continue;

			material.Set( $"g_t{slot}", loaded );
			material.Set( $"g_fl{slot}Srgb", ShaderBitmaps.IsSrgb( texture.Bitmap, texture.BitmapIndex ) ? 1f : 0f );
			hasBase |= slot == "BaseMap";
		}

		if ( !hasBase )
			return null;

		var frames = particle.Frames.Select( f => new Vector4( f.X, f.Y, f.Z, f.W ) ).ToArray();
		return new Halo3ParticleDefinition
		{
			Tag = particle,
			Material = material,
			Adds = blend is 1 or 5 or 6 or 7 or 8,
			Frames = frames.Length > 0 ? frames : [new Vector4( 0, 0, 1, 1 )],
			SpriteRect = new Vector4( particle.SpriteRectangle.X, particle.SpriteRectangle.Y, particle.SpriteRectangle.Z, particle.SpriteRectangle.W ),
			FrameBlend = shader.Option( "frame_blend" ) == "on",
			CollisionEffect = particle.Attachments.FirstOrDefault( a => a.Trigger == 1 && EffectTag.IsEffect( a.Type ) )?.Type,
			DeathEffect = particle.Attachments.FirstOrDefault( a => a.Trigger == 2 && EffectTag.IsEffect( a.Type ) )?.Type,
		};
	}
}
