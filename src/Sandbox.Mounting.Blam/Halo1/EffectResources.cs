using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// One sequence of a particle bitmap as a sprite for <see cref="ParticleSpriteRenderer"/>,
/// <c>mount://haloce/&lt;bitmap tag&gt;__s&lt;sequence&gt;.sprite</c>. Its first animation, "default",
/// plays every sprite of the sequence (one frame a second; the renderer's playback speed sets the
/// rate); "frame&lt;n&gt;" animations hold one sprite each, for particles that start on a random frame;
/// "then&lt;k&gt;" plays this sequence once, then loops sequence k. That's a Halo particle's life: its
/// initial sequence, then its looping one (impact sparks: a streak shrinking into a dot).
/// "from&lt;k&gt;" loop every sprite starting at sprite k (see <see cref="StartAnimation"/>), so renderers
/// playing the same sequence needn't show the same sprite.
/// </summary>
/// <remarks>
/// The particle renderer draws whole textures, so each sprite is cut out of its sheet into a
/// texture of its own.
/// </remarks>
sealed class EffectSpriteLoader( string mapPath, uint bitmapTagId, int sequence ) : HaloCeLoader
{
	Sprite _sprite;

	public static string ResourceName( string bitmapTagPath, int sequence ) => $"{HaloCeMount.ResourcePath( bitmapTagPath )}__s{sequence}.sprite";

	public static string FullPath( string bitmapTagPath, int sequence ) => $"mount://haloce/{ResourceName( bitmapTagPath, sequence )}";

	/// <summary>
	/// The sprites of a sprite's sequence (its "default" animation).
	/// </summary>
	public static int FrameCount( Sprite sprite ) => sprite.Animations[0].Frames.Count;

	/// <summary>
	/// Looping animations starting part way into the sequence: at 1/4, 2/4 and 3/4 of it.
	/// </summary>
	const int StartPoints = 4;

	/// <summary>
	/// The animation looping a sequence of <paramref name="frames"/> sprites from about
	/// <paramref name="fraction"/> (0-1) of the way in.
	/// </summary>
	public static string StartAnimation( int frames, float fraction )
	{
		var point = Math.Clamp( (int)(fraction * StartPoints), 0, StartPoints - 1 );
		return point == 0 || frames < StartPoints ? "default" : $"from{point * frames / StartPoints}";
	}

	protected override object Load()
	{
		if ( _sprite is not null )
			return _sprite;

		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( bitmapTagId );
		var frames = SpriteTextures.Sequence( cache, tag, sequence );
		if ( frames.Count == 0 )
			return null;

		// Halo places and turns a sprite around its registration point, not its centre: the dirt
		// burst's sits low, so most of the burst stands above the particle instead of half of it
		// underground. s&box keeps one origin per animation, so single sprites get their own and
		// the sequences their first sprite's (a sequence's registration points barely differ).
		var origins = SpriteTextures.Origins( cache, tag, sequence );
		var animations = new List<Sprite.Animation>
		{
			new() { Name = "default", FrameRate = 1, Origin = origins[0], Frames = frames.Select( t => new Sprite.Frame { Texture = t } ).ToList() },
		};

		for ( var i = 0; i < frames.Count; i++ )
			animations.Add( new Sprite.Animation { Name = $"frame{i}", FrameRate = 1, Origin = origins[i], Frames = [new Sprite.Frame { Texture = frames[i] }] } );

		if ( frames.Count >= StartPoints )
		{
			for ( var point = 1; point < StartPoints; point++ )
			{
				var first = point * frames.Count / StartPoints;
				animations.Add( new Sprite.Animation { Name = $"from{first}", FrameRate = 1, Origin = origins[first], Frames = frames.Skip( first ).Concat( frames.Take( first ) ).Select( t => new Sprite.Frame { Texture = t } ).ToList() } );
			}
		}

		var sequences = new BitmapTag( cache, tag ).Sequences.Count;
		for ( var k = 0; k < sequences; k++ )
		{
			var loop = k == sequence ? frames : SpriteTextures.Sequence( cache, tag, k );
			if ( loop.Count == 0 )
				continue;

			animations.Add( new Sprite.Animation
			{
				Name = $"then{k}",
				FrameRate = 1,
				Origin = origins[0],
				Frames = frames.Concat( loop ).Select( t => new Sprite.Frame { Texture = t } ).ToList(),
				LoopStart = frames.Count,
			} );
		}

		return _sprite = new Sprite { Animations = animations };
	}
}

/// <summary>
/// Textures cut out of particle sprite sheets, made once per sheet and sequence. They only live in
/// the sprites that hold them, so they aren't registered as resources of their own.
/// </summary>
static class SpriteTextures
{
	static readonly Dictionary<(string Map, uint Tag, int Sequence), (List<Texture> Textures, List<Vector2> Origins)> Cache = new();

	/// <summary>
	/// The sprites of one sequence of a bitmap tag as textures. A sequence without sprites stands
	/// for its whole bitmaps.
	/// </summary>
	public static List<Texture> Sequence( CacheFile cache, TagEntry bitmapTag, int sequence ) => Get( cache, bitmapTag, sequence ).Textures;

	/// <summary>
	/// Where each sprite of <see cref="Sequence"/> is anchored, as a fraction of its size from its
	/// top left corner (s&amp;box's sprite origin): its registration point. Whole bitmaps are
	/// anchored at the centre.
	/// </summary>
	public static List<Vector2> Origins( CacheFile cache, TagEntry bitmapTag, int sequence ) => Get( cache, bitmapTag, sequence ).Origins;

	static (List<Texture> Textures, List<Vector2> Origins) Get( CacheFile cache, TagEntry bitmapTag, int sequence )
	{
		var key = (cache.FilePath, bitmapTag.Id, sequence);
		lock ( Cache )
		{
			if ( Cache.TryGetValue( key, out var cached ) )
				return cached;
		}

		var textures = new List<Texture>();
		var origins = new List<Vector2>();
		var bitmap = new BitmapTag( cache, bitmapTag );
		var pages = new Dictionary<int, RgbaImage>();
		RgbaImage Page( int index )
		{
			if ( index < 0 || index >= bitmap.Bitmaps.Count )
				return null;
			if ( !pages.TryGetValue( index, out var page ) )
				pages[index] = page = SpriteImages.Decode( cache, bitmap.Bitmaps[index] );
			return page;
		}

		var images = new List<RgbaImage>();
		if ( bitmap.Sequences.Count > 0 )
		{
			var seq = bitmap.Sequences[Math.Clamp( sequence, 0, bitmap.Sequences.Count - 1 )];
			if ( seq.Sprites.Count > 0 )
			{
				foreach ( var sprite in seq.Sprites )
				{
					var page = Page( sprite.BitmapIndex );
					if ( page is not null )
					{
						images.Add( SpriteImages.Crop( page, sprite ) );
						var width = sprite.Right - sprite.Left;
						var height = sprite.Bottom - sprite.Top;
						origins.Add( width > 0 && height > 0 ? new Vector2( Math.Clamp( sprite.RegistrationX / width, 0, 1 ), Math.Clamp( sprite.RegistrationY / height, 0, 1 ) ) : new Vector2( 0.5f, 0.5f ) );
					}
				}
			}
			else
			{
				for ( var i = 0; i < seq.BitmapCount; i++ )
				{
					var page = Page( seq.FirstBitmapIndex + i );
					if ( page is not null )
						images.Add( page );
				}
			}
		}
		else if ( Page( 0 ) is { } whole )
		{
			images.Add( whole );
		}

		foreach ( var image in images )
			textures.Add( Create( image ) );

		while ( origins.Count < textures.Count )
			origins.Add( new Vector2( 0.5f, 0.5f ) );

		lock ( Cache )
			Cache[key] = (textures, origins);

		return (textures, origins);
	}

	/// <summary>
	/// An RGBA texture with box filtered mips (smallest first, as s&amp;box wants them).
	/// </summary>
	static Texture Create( RgbaImage image )
	{
		var level = image.Pixels;
		var levels = new List<byte[]> { level };
		int w = image.Width, h = image.Height;
		while ( w > 1 || h > 1 )
		{
			int nw = Math.Max( 1, w / 2 ), nh = Math.Max( 1, h / 2 );
			var next = new byte[nw * nh * 4];
			for ( var y = 0; y < nh; y++ )
			for ( var x = 0; x < nw; x++ )
			for ( var c = 0; c < 4; c++ )
			{
				int x0 = Math.Min( x * 2, w - 1 ), x1 = Math.Min( x * 2 + 1, w - 1 );
				int y0 = Math.Min( y * 2, h - 1 ), y1 = Math.Min( y * 2 + 1, h - 1 );
				var sum = level[(y0 * w + x0) * 4 + c] + level[(y0 * w + x1) * 4 + c] + level[(y1 * w + x0) * 4 + c] + level[(y1 * w + x1) * 4 + c];
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

		return Texture.Create( image.Width, image.Height, ImageFormat.RGBA8888 )
			.WithData( data )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}
}
