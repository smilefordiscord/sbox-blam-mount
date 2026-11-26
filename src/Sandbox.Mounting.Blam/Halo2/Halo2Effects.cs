using Blam.Halo2;
using Blam.Halo2.Tags;
using Halo1Format = Blam.Halo1.Tags.BitmapFormat;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Registers Halo 2's effects as prefabs, the convention the Halo CE mount and the game agreed on: built from engine
/// components and tags only, so they also work in scenes and prefabs a game saves; <see cref="Halo2ParticleWorld"/>
/// plays them (<see cref="Halo2ParticleEffects"/> describes the marking).
/// </summary>
/// <remarks>
/// <para><c>mount://halo2/&lt;effect tag&gt;.prefab</c> plays an effect once and removes itself (its loose particles
/// finish on their own); <c>&lt;effect tag&gt;__loop.prefab</c> repeats it forever (effects objects carry, see
/// <see cref="Halo2EffectBuilder"/>); <c>&lt;effect tag&gt;__fp.prefab</c>, for effects whose particle systems differ
/// there, is the effect as the local player sees it in their own first person view (their weapon's muzzle flash).
/// The root's +X is the effect's forward. Each location (marker) the effect uses
/// is a child named after the marker ("" is the root): move those children onto the model attachments of the same
/// name. Halo aims the special locations of effects made where something hits itself: "gravity" points down,
/// "up" up, "forward" (the shot's direction) along the root's -X; "normal", "backward" and "reflection" are the root.</para>
/// <para><c>&lt;object tag&gt;__attachments.prefab</c>: what an object carries (effects, light volumes, lens flares,
/// contrails, looping sounds), under children named after its markers (see <see cref="ObjectEffects"/>);
/// <c>&lt;projectile tag&gt;__projectile.prefab</c>: the same with the projectile's model, the projectile in flight.</para>
/// <para>Resources those use: <c>&lt;particle tag&gt;__sprite.vmat</c>, the particle's look in halo1_sprite.shader
/// (blend and tint per its particle shader template), and <c>&lt;particle tag&gt;__sprite.sprite</c>, its bitmap
/// sequences cut into one texture per sprite (for games; the runtime cuts its own), and
/// <c>&lt;contrail tag&gt;__contrail.vmat</c> (<see cref="ContrailMaterials"/>).</para>
/// </remarks>
static class Halo2Effects
{
	public const string Ident = "halo2";

	public static string PrefabName( string tagPath, bool looping ) => $"{Halo2Mount.ResourcePath( tagPath )}{(looping ? "__loop" : "")}.prefab";

	// The effect seen in its local player's own first person view, for effects whose particle systems differ there.
	public static string FirstPersonPrefabName( string tagPath ) => $"{Halo2Mount.ResourcePath( tagPath )}__fp.prefab";

	public static string PrefabPath( string tagPath, bool looping ) => $"mount://{Ident}/{PrefabName( tagPath, looping )}";

	public static string MaterialName( string particleTagPath ) => $"{Halo2Mount.ResourcePath( particleTagPath )}__sprite";

	public static string MaterialPath( string particleTagPath ) => $"mount://{Ident}/{MaterialName( particleTagPath )}.vmat";

	public static string SpriteName( string particleTagPath ) => $"{Halo2Mount.ResourcePath( particleTagPath )}__sprite.sprite";

	public static string SpritePath( string particleTagPath ) => $"mount://{Ident}/{SpriteName( particleTagPath )}";

	public static string ProjectilePrefabName( string projectileTagPath ) => $"{Halo2Mount.ResourcePath( projectileTagPath )}__projectile.prefab";

	/// <summary>
	/// Registers the effects and particles whose data this map holds (shared.map's for the multiplayer maps), unless
	/// an earlier map registered them.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var mapPath = cache.FilePath;
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Cache != cache || tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) )
				continue;

			if ( tag.Class == EffectTag.Class )
			{
				foreach ( var looping in new[] { false, true } )
				{
					var name = PrefabName( tag.Path, looping );
					if ( registered.Add( name ) )
						context.Add( ResourceType.PrefabFile, name, new Halo2EffectPrefabLoader( mapPath, tag.Id, looping ) );
				}

				var firstPerson = FirstPersonPrefabName( tag.Path );
				if ( new EffectTag( tag ).DiffersInFirstPerson && registered.Add( firstPerson ) )
					context.Add( ResourceType.PrefabFile, firstPerson, new Halo2EffectPrefabLoader( mapPath, tag.Id, looping: false, firstPerson: true ) );

				Halo2ParticleLibrary.Register( cache, tag );
			}
			else if ( ObjectTag.IsObject( tag ) )
			{
				// What the object carries, for games (ObjectEffects); a projectile also with its model.
				var attachments = ObjectEffects.Read( tag );
				var carries = attachments.Count > 0;
				var name = ObjectEffects.PrefabName( tag.Path );
				if ( carries && registered.Add( name ) )
					context.Add( ResourceType.PrefabFile, name, new ObjectAttachmentsPrefabLoader( mapPath, tag.Id ) );

				// The first person variant, for an object carrying effects that differ in its player's first person view.
				var firstPerson = ObjectEffects.FirstPersonPrefabName( tag.Path );
				if ( attachments.Any( a => EffectTag.IsEffect( a.Type ) && new EffectTag( a.Type ).DiffersInFirstPerson ) && registered.Add( firstPerson ) )
					context.Add( ResourceType.PrefabFile, firstPerson, new ObjectAttachmentsPrefabLoader( mapPath, tag.Id, firstPerson: true ) );

				RegisterRoles( context, tag, registered );

				var projectile = ProjectilePrefabName( tag.Path );
				if ( new ObjectTag( tag ).Type == ObjectType.Projectile && (carries || ObjectEffects.ModelPath( tag ) is not null) && registered.Add( projectile ) )
					context.Add( ResourceType.PrefabFile, projectile, new ObjectAttachmentsPrefabLoader( mapPath, tag.Id, withModel: true ) );
			}
			else if ( ContrailTag.IsContrail( tag ) )
			{
				var name = ContrailMaterials.ResourceName( tag.Path );
				if ( registered.Add( name ) )
					context.Add( ResourceType.Material, name, new ContrailMaterialLoader( mapPath, tag.Id ) );
			}
			else if ( tag.Class == ParticleTag.Class )
			{
				if ( registered.Add( MaterialName( tag.Path ) ) )
				{
					context.Add( ResourceType.Material, MaterialName( tag.Path ), new Halo2ParticleMaterialLoader( mapPath, tag.Id ) );
					context.Add( ResourceType.GameResource, SpriteName( tag.Path ), new Halo2ParticleSpriteLoader( mapPath, tag.Id ) );
				}
			}
		}
	}

	// The object's role aliases (EffectRoles): <object>__<role>.prefab for an effect, .sound for a sound.
	static void RegisterRoles( MountContext context, TagEntry tag, HashSet<string> registered )
	{
		foreach ( var role in EffectRoles.For( tag ) )
		{
			if ( EffectTag.IsEffect( role.Tag ) )
			{
				var name = $"{role.Name}.prefab";
				if ( registered.Add( name ) )
					context.Add( ResourceType.PrefabFile, name, new Halo2EffectPrefabLoader( role.Tag.Cache.FilePath, role.Tag.Id, looping: false, role.FirstPerson ) );
			}
			else if ( registered.Add( $"{role.Name}.sound" ) )
			{
				Halo2Mount.Current.Sounds?.Alias( context, $"{role.Name}.sound", role.Tag.Path );
			}
		}
	}

	/// <summary>
	/// Whether a resource path is registered with the Halo 2 mount.
	/// </summary>
	public static bool Exists( string path ) => Sandbox.Mounting.Directory.Get( Ident )?.GetByPath( path ) is not null;

	/// <summary>
	/// How halo1_sprite.shader draws a particle shader template: its blend mode (0 alpha blend, 7 alpha-multiply add:
	/// the colour times its coverage added) and how much of Halo's nonlinear "hot white" tint it takes, or null for
	/// templates it can't draw (distortion).
	/// </summary>
	/// <remarks>Template math: additive: o = s x c with
	/// SRCALPHA/ONE (adds rgb x a); additive_tint: lerp( tint term, s x c, modulation factor ); alpha_blend_tint: the
	/// tint term blended; alpha_multiply_add and the glass template (premultiplied, ONE/INVSRCALPHA) as alpha blends.</remarks>
	public static (int BlendMode, float NonlinearTint)? Look( ParticleTag particle ) => particle.TemplateName switch
	{
		"particle_alpha_blend" => (0, 0),
		"particle_alpha_blend_tint" => (0, 1),
		"particle_additive" => (7, 0),
		"particle_additive_tint" => (7, 1 - Math.Clamp( particle.Value( "modulation_factor", 0.5f ), 0, 1 )),
		"particle_alpha_multiply_add" or "particle_alpha_test_no_lighting_fixed_for_glass" => (0, 0),
		_ => null,
	};

	/// <summary>
	/// The bitmap sequences a particle draws: from its first sequence, its sequence count of them (at least one),
	/// those past the bitmap's last left out.
	/// </summary>
	public static List<int> Sequences( ParticleTag particle, BitmapTag bitmap )
	{
		var first = Math.Max( particle.FirstSequence, 0 );
		var count = Math.Max( particle.SequenceCount, 1 );
		var sequences = new List<int>();
		for ( var i = first; i < first + count && i < Math.Max( bitmap.Sequences.Count, 1 ); i++ )
			sequences.Add( i );
		return sequences;
	}
}

/// <summary>
/// A particle's look in halo1_sprite.shader (see <see cref="Halo2Effects.Look"/>): blend mode, nonlinear tint and its
/// random mirroring.
/// </summary>
sealed class Halo2ParticleMaterialLoader( string mapPath, uint tagId ) : Halo2Loader
{
	public const string ShaderPath = "shaders/halo1_sprite.shader";

	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( !ParticleTag.IsParticle( tag ) )
			return null;

		var particle = new ParticleTag( tag );
		if ( Halo2Effects.Look( particle ) is not { } look )
			return null;

		var material = Material.Create( Path, ShaderPath );
		if ( material is null )
			return null;

		material.SetFeature( "F_BLEND_MODE", look.BlendMode );
		material.Set( "g_flNonlinearTint", look.NonlinearTint );
		material.SetFeature( "F_ZSPRITE", 0 );
		material.Set( "g_flZSpriteScale", 0f );
		material.Set( "g_flHaloSpriteMinimumSize", 0f );
		material.Set( "g_vHaloSpriteFadeSize", Vector2.Zero );
		material.Set( "g_vHaloSpriteMirror", new Vector2( particle.RandomUMirror ? 1 : 0, particle.RandomVMirror ? 1 : 0 ) );
		return material;
	}
}

/// <summary>
/// A particle's bitmap sequences (<see cref="Halo2Effects.Sequences"/>) as one sprite: an animation per sequence,
/// "default" the first and "sequence&lt;n&gt;" the rest, each sprite cut into a texture of its own, played once and
/// held (one frame a second; the renderer's playback speed sets the rate). A sequence without sprites draws its
/// whole bitmaps.
/// </summary>
sealed class Halo2ParticleSpriteLoader( string mapPath, uint tagId ) : Halo2Loader
{
	Sprite _sprite;

	protected override object Load()
	{
		if ( _sprite is not null )
			return _sprite;

		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( !ParticleTag.IsParticle( tag ) || new ParticleTag( tag ).Bitmap is not { Address: not 0 } bitmapTag )
			return null;

		var particle = new ParticleTag( tag );
		var bitmap = new BitmapTag( bitmapTag );
		var pages = new Dictionary<int, (int Width, int Height, byte[] Pixels)>();
		(int Width, int Height, byte[] Pixels)? Page( int index )
		{
			if ( index < 0 || index >= bitmap.Bitmaps.Count )
				return null;
			if ( !pages.TryGetValue( index, out var page ) )
			{
				var decoded = Decode( bitmap.Bitmaps[index] );
				if ( decoded is null )
					return null;
				pages[index] = page = decoded.Value;
			}
			return page;
		}

		var animations = new List<Sprite.Animation>();
		foreach ( var s in Halo2Effects.Sequences( particle, bitmap ) )
		{
			var frames = new List<(Texture Texture, Vector2 Origin)>();
			var sequence = s < bitmap.Sequences.Count ? bitmap.Sequences[s] : null;
			if ( sequence is { Sprites.Count: > 0 } )
			{
				foreach ( var sprite in sequence.Sprites )
				{
					if ( Page( sprite.BitmapIndex ) is not { } page )
						continue;

					var (texture, origin) = Cut( page, sprite );
					if ( texture is not null )
						frames.Add( (texture, origin) );
				}
			}
			else
			{
				var first = sequence?.FirstBitmapIndex ?? 0;
				var count = sequence is null ? 1 : Math.Max( sequence.BitmapCount, 1 );
				for ( var i = first; i < first + count; i++ )
				{
					if ( Page( i ) is { } page )
						frames.Add( (Create( page.Width, page.Height, page.Pixels ), new Vector2( 0.5f, 0.5f )) );
				}
			}

			if ( frames.Count == 0 )
				continue;

			var name = animations.Count == 0 ? "default" : $"sequence{s}";
			animations.Add( new Sprite.Animation { Name = name, FrameRate = 1, Origin = frames[0].Origin, LoopMode = Sprite.LoopMode.None, Frames = frames.Select( f => new Sprite.Frame { Texture = f.Texture } ).ToList() } );
		}

		if ( animations.Count == 0 )
			return null;

		return _sprite = new Sprite { Animations = animations };
	}

	/// <summary>
	/// The top level of a 2D bitmap as RGBA rows, or null for formats that can't be decoded.
	/// </summary>
	internal static (int Width, int Height, byte[] Pixels)? Decode( BitmapData bitmap )
	{
		if ( bitmap.Type != BitmapType.Texture2D || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.ElementSize <= 0 )
			return null;

		var format = (Halo1Format)bitmap.Format;
		if ( (ushort)bitmap.Format > (ushort)Halo1Format.A8R8G8B8 && !bitmap.IsBlockCompressed || !global::Blam.Halo1.BitmapDecoder.CanDecodeImage( format ) )
			return null;

		var pixels = Halo2Mount.Current.Textures.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );
		var rgba = new byte[bitmap.Width * bitmap.Height * 4];
		global::Blam.Halo1.BitmapDecoder.DecodeImage( format, pixels.AsSpan( 0, bitmap.GetLevelSize( 0 ) ), rgba, bitmap.Width, bitmap.Height );
		return (bitmap.Width, bitmap.Height, rgba);
	}

	/// <summary>
	/// A sprite's rectangle of its page (rounded out to whole pixels) as a texture, and its registration point as a
	/// fraction of its size from the top left (s&amp;box's sprite origin).
	/// </summary>
	internal static (Texture Texture, Vector2 Origin) Cut( (int Width, int Height, byte[] Pixels) page, BitmapSprite sprite )
	{
		var x0 = Math.Clamp( (int)MathF.Floor( sprite.Left * page.Width + 0.001f ), 0, page.Width - 1 );
		var y0 = Math.Clamp( (int)MathF.Floor( sprite.Top * page.Height + 0.001f ), 0, page.Height - 1 );
		var x1 = Math.Clamp( (int)MathF.Ceiling( sprite.Right * page.Width - 0.001f ), x0 + 1, page.Width );
		var y1 = Math.Clamp( (int)MathF.Ceiling( sprite.Bottom * page.Height - 0.001f ), y0 + 1, page.Height );
		var width = x1 - x0;
		var height = y1 - y0;
		var pixels = new byte[width * height * 4];
		for ( var y = 0; y < height; y++ )
			Array.Copy( page.Pixels, ((y0 + y) * page.Width + x0) * 4, pixels, y * width * 4, width * 4 );

		var w = sprite.Right - sprite.Left;
		var h = sprite.Bottom - sprite.Top;
		var origin = w > 0 && h > 0 ? new Vector2( Math.Clamp( sprite.RegistrationX / w, 0, 1 ), Math.Clamp( sprite.RegistrationY / h, 0, 1 ) ) : new Vector2( 0.5f, 0.5f );
		return (Create( width, height, pixels ), origin);
	}

	/// <summary>
	/// An RGBA texture with box filtered mips (smallest first, as s&amp;box wants them).
	/// </summary>
	internal static Texture Create( int width, int height, byte[] pixels )
	{
		var level = pixels;
		var levels = new List<byte[]> { level };
		int w = width, h = height;
		while ( w > 1 || h > 1 )
		{
			int nw = Math.Max( 1, w / 2 ), nh = Math.Max( 1, h / 2 );
			var next = new byte[nw * nh * 4];
			for ( var y = 0; y < nh; y++ )
			for ( var x = 0; x < nw; x++ )
			for ( var c = 0; c < 4; c++ )
			{
				int xa = Math.Min( x * 2, w - 1 ), xb = Math.Min( x * 2 + 1, w - 1 );
				int ya = Math.Min( y * 2, h - 1 ), yb = Math.Min( y * 2 + 1, h - 1 );
				var sum = level[(ya * w + xa) * 4 + c] + level[(ya * w + xb) * 4 + c] + level[(yb * w + xa) * 4 + c] + level[(yb * w + xb) * 4 + c];
				next[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
			}

			levels.Add( next );
			level = next;
			w = nw;
			h = nh;
		}

		var data = new byte[levels.Sum( l => l.Length )];
		var offset = 0;
		for ( var i = levels.Count - 1; i >= 0; i-- )
		{
			levels[i].CopyTo( data, offset );
			offset += levels[i].Length;
		}

		return Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithData( data )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}
}

/// <summary>
/// Builds a prefab with <see cref="PrefabBuilder"/> on the main thread (see the Halo CE mount's HaloPrefabLoader):
/// the root built disabled, so nothing plays while it's put together, and saved enabled.
/// </summary>
abstract class Halo2PrefabLoader : Halo2Loader
{
	PrefabFile _prefab;

	protected abstract string MapPath { get; }

	protected abstract void Build( CacheFile cache, GameObject root );

	protected override Task<object> LoadAsync()
	{
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected sealed override object Load()
	{
		if ( _prefab is not null )
			return _prefab;

		var cache = Host.GetCache( MapPath );
		var builder = new PrefabBuilder().WithName( Path );
		using ( builder.Scope() )
		{
			var root = new GameObject( false, Name );
			Build( cache, root );
		}

		_prefab = builder.Create();
		if ( _prefab?.RootObject is { } json )
			json["Enabled"] = true;

		return _prefab;
	}

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}

sealed class Halo2EffectPrefabLoader( string mapPath, uint effectTagId, bool looping, bool firstPerson = false ) : Halo2PrefabLoader
{
	protected override string MapPath => mapPath;

	protected override void Build( CacheFile cache, GameObject root )
	{
		var tag = cache.GetTag( effectTagId );
		if ( !EffectTag.IsEffect( tag ) )
			return;

		var effect = new EffectTag( tag );
		var length = Halo2EffectBuilder.Build( effect, root, looping, firstPerson: firstPerson );

		// A one-shot effect's root goes once it's played out (its loose particles stay in the world till they die).
		if ( !looping )
			root.Components.Get<TemporaryEffect>( FindMode.EverythingInSelf ).DestroyAfterSeconds = length + 0.1f;
	}
}
