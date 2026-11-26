using Blam.Halo1;
using Blam.Halo1.Tags;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Halo's decals (bullet holes, blood, scorches) as prefabs a game can spawn where something hits:
/// <c>mount://haloce/&lt;deca tag path&gt;.prefab</c>, a GameObject with s&amp;box's
/// <see cref="Decal"/> component. Spawn it at the hit point facing into the surface, the way
/// s&amp;box's own weapons place impact decals (<c>Rotation.LookAt( -tr.Normal )</c>); the decal
/// projects along the object's forward axis.
/// </summary>
/// <remarks>
/// Each variation of a decal (the first sprite of each sequence of its sprite sheet, see
/// <see cref="DecalVariations"/>) is a decal definition, <c>&lt;deca tag path&gt;.decal</c> for the
/// first and <c>&lt;deca tag path&gt;_&lt;n&gt;.decal</c> for the rest, and the component picks one at
/// random, as Halo does. The size follows Halo's radius (random between its bounds), the color and
/// intensity are random between their bounds, the spin random unless the decal is flagged not to,
/// and decals with a lifetime fade out at the end of it and are removed. Decals are transient:
/// past the engine's decal limit the oldest go first. A decal's chain (a plasma burn's inner and
/// outer glows) is in the same prefab, one child each.
///
/// Halo only puts decals on level geometry, and its environment shader (halo1_environment.shader)
/// blends them in as Halo's rasterizer does: in the layer's pass, by the decal's framebuffer blend
/// function, with the color and intensity (the component's color mix). It reads the layer and the
/// blend function from the decal's sort layer (<see cref="SortLayer"/>). The maps are Halo's own
/// texels; the blend mode s&amp;box's own shaders would use is the nearest one.
///
/// The scenario's own decals are drawn by <see cref="DecalModel"/> instead.
/// </remarks>
static class DecalPrefabs
{
	static readonly global::Blam.TagClass DecalClass = new( "deca" );

	/// <summary>
	/// Decal chains are short (a burn and two glows); this guards against loops.
	/// </summary>
	public const int MaximumChainLength = 4;

	/// <summary>
	/// Tag on every decal prefab's root.
	/// </summary>
	public const string Tag = "halo_decal";

	/// <summary>
	/// The resource name of one variation's decal definition. The first is named after the tag.
	/// </summary>
	public static string DefinitionName( string tagPath, int index ) =>
		index == 0 ? $"{HaloCeMount.ResourcePath( tagPath )}.decal" : $"{HaloCeMount.ResourcePath( tagPath )}_{index}.decal";

	/// <summary>
	/// Registers the decal tags of a map that haven't been registered by an earlier map.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered, Sandbox.Diagnostics.Logger log )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != DecalClass || string.IsNullOrEmpty( tag.Path ) || registered.Contains( tag.Path ) )
				continue;

			int count;
			try
			{
				count = DecalVariations.Read( cache, new DecalTag( cache, tag ) ).Count;
			}
			catch ( Exception e )
			{
				log.Warning( e, $"Couldn't read decal {tag.Path}: {e.Message}" );
				continue;
			}

			if ( count == 0 )
				continue;

			registered.Add( tag.Path );
			for ( var i = 0; i < count; i++ )
				context.Add( ResourceType.GameResource, DefinitionName( tag.Path, i ), new DecalDefinitionLoader( cache.FilePath, tag.Id, i ) );

			context.Add( ResourceType.PrefabFile, HaloCeMount.ResourcePath( tag.Path ), new DecalPrefabLoader( cache.FilePath, tag.Id ) );
		}
	}

	/// <summary>
	/// The sort layer that carries a decal's layer and framebuffer blend function to the
	/// environment shader: layer * 8 + function. Layers sort in Halo's order within a pass.
	/// </summary>
	public static uint SortLayer( DecalTag decal ) => (uint)decal.Layer * 8 + (uint)decal.BlendFunction;

	/// <summary>
	/// A variation's padded width and height in s&amp;box units, at the decal's largest radius. Halo
	/// scales a sprite by its size in pixels: a sprite as large as the decal's maximum sprite extent
	/// spans the radius; with "preserve aspect" its height is its width times the page's aspect. A
	/// whole bitmap fills the radius square, or keeps its aspect with "preserve aspect".
	/// </summary>
	public static (float Width, float Height) Size( DecalTag decal, DecalVariation variation )
	{
		var radius = decal.RadiusMax * HaloCeMount.WorldScale;
		var bitmap = variation.Bitmap;
		if ( !variation.Sprite )
			return (2 * radius, 2 * radius * (decal.PreserveAspect ? (float)bitmap.Height / bitmap.Width : 1));

		var scale = radius / decal.MaximumSpriteExtent;
		// Halo's aspect scale: the sprite's width over height in page coordinates, times the page's height over width.
		var pageAspect = (float)bitmap.Height / bitmap.Width;
		var aspect = decal.PreserveAspect ? (float)variation.Width / variation.Height * pageAspect * pageAspect : 1;
		return (variation.PaddedWidth * scale, variation.PaddedHeight * scale * aspect);
	}

	/// <summary>
	/// The decal definitions of a decal tag that are registered and load.
	/// </summary>
	public static List<DecalDefinition> LoadDefinitions( HaloCeMount host, string tagPath, int count )
	{
		var mount = Sandbox.Mounting.Directory.Get( host.Ident );
		var definitions = new List<DecalDefinition>();
		for ( var i = 0; i < count; i++ )
		{
			var path = $"mount://{host.Ident}/{DefinitionName( tagPath, i )}";
			if ( mount?.GetByPath( path ) is not null && GameResource.Load<DecalDefinition>( path ) is { } definition )
				definitions.Add( definition );
		}

		return definitions;
	}
}

/// <summary>
/// One variation of a decal as a decal definition: its sprite cut out of the sheet as a texture,
/// at the decal's largest size.
/// </summary>
sealed class DecalDefinitionLoader( string mapPath, uint tagId, int index ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var decal = new DecalTag( cache, cache.GetTag( tagId ) );
		var variations = DecalVariations.Read( cache, decal );
		if ( index >= variations.Count )
			return null;

		var variation = variations[index];
		var rgba = DecalVariations.Cut( cache, variation, Neutral( decal.BlendFunction ) );
		if ( rgba is null )
			return null;

		var (width, height) = DecalPrefabs.Size( decal, variation );

		// The color is the decal component's, picked between the decal's bounds.
		return new DecalDefinition
		{
			ColorTexture = CreateTexture( rgba, variation.PaddedWidth, variation.PaddedHeight ),
			Tint = Color.White,
			BlendMode = decal.BlendFunction switch
			{
				FramebufferBlendFunction.Multiply or FramebufferBlendFunction.DoubleMultiply or FramebufferBlendFunction.ComponentMin => DecalBlendMode.Multiply,
				FramebufferBlendFunction.AlphaBlend => DecalBlendMode.Alpha,
				_ => DecalBlendMode.Additive,
			},
			Width = width,
			Height = height,
		};
	}

	/// <summary>
	/// The texel that leaves the surface as it is under a blend function, for the padding around a
	/// sprite: white for multiply and min, Halo's 50% gray for double multiply, black and clear for
	/// the rest.
	/// </summary>
	static byte[] Neutral( FramebufferBlendFunction blend ) => blend switch
	{
		FramebufferBlendFunction.Multiply or FramebufferBlendFunction.ComponentMin => [255, 255, 255, 255],
		FramebufferBlendFunction.DoubleMultiply => [128, 128, 128, 255],
		_ => [0, 0, 0, 0],
	};

	/// <summary>
	/// Each 8-bit value stored so that sRGB decoding gives it back.
	/// </summary>
	static readonly byte[] SrgbEncoded = Enumerable.Range( 0, 256 ).Select( v =>
	{
		var linear = v / 255.0;
		var srgb = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow( linear, 1 / 2.4 ) - 0.055;
		return (byte)Math.Round( Math.Clamp( srgb, 0, 1 ) * 255 );
	} ).ToArray();

	/// <summary>
	/// An RGBA texture with a full mip chain, box filtered in Halo's gamma space. s&amp;box reads a
	/// decal's color map as sRGB and decodes it, but the environment shader blends decals in
	/// Halo's gamma space, so each level's color is stored encoded: decoding gives back Halo's values.
	/// </summary>
	Texture CreateTexture( byte[] rgba, int width, int height )
	{
		var levels = new List<byte[]> { rgba };
		int w = width, h = height;
		while ( w > 1 || h > 1 )
		{
			var nw = Math.Max( 1, w / 2 );
			var nh = Math.Max( 1, h / 2 );
			var source = levels[^1];
			var level = new byte[nw * nh * 4];
			for ( var y = 0; y < nh; y++ )
			for ( var x = 0; x < nw; x++ )
			for ( var c = 0; c < 4; c++ )
			{
				int x0 = Math.Min( x * 2, w - 1 ), x1 = Math.Min( x * 2 + 1, w - 1 );
				int y0 = Math.Min( y * 2, h - 1 ), y1 = Math.Min( y * 2 + 1, h - 1 );
				var sum = source[(y0 * w + x0) * 4 + c] + source[(y0 * w + x1) * 4 + c] + source[(y1 * w + x0) * 4 + c] + source[(y1 * w + x1) * 4 + c];
				level[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
			}

			levels.Add( level );
			w = nw;
			h = nh;
		}

		foreach ( var level in levels )
		{
			for ( var i = 0; i < level.Length; i++ )
			{
				if ( i % 4 != 3 )
					level[i] = SrgbEncoded[level[i]];
			}
		}

		// s&box wants the smallest level first.
		levels.Reverse();
		return Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithName( Path )
			.WithData( levels.SelectMany( l => l ).ToArray() )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}
}

/// <summary>
/// A decal tag (and its chain) as a prefab with a <see cref="Decal"/> component per decal.
/// </summary>
sealed class DecalPrefabLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	PrefabFile _prefab;

	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var builder = new PrefabBuilder().WithName( Path );
		using ( builder.Scope() )
		{
			var root = new GameObject( true, Name );
			root.Tags.Add( DecalPrefabs.Tag );

			var seen = new HashSet<uint>();
			var tag = cache.GetTag( tagId );
			for ( var depth = 0; tag is not null && depth < DecalPrefabs.MaximumChainLength && seen.Add( tag.Id ); depth++ )
			{
				var decal = new DecalTag( cache, tag );
				var target = depth == 0 ? root : new GameObject( root, true, System.IO.Path.GetFileName( tag.Path ) );
				AddDecal( cache, target, tag, decal );
				tag = decal.NextInChain;
			}

			// Removes the object once its decals have faded, or been dropped past the decal limit.
			root.AddComponent<TemporaryEffect>().DestroyAfterSeconds = 1;
		}

		_prefab = builder.Create();
		return _prefab;
	}

	void AddDecal( CacheFile cache, GameObject target, TagEntry tag, DecalTag decal )
	{
		var count = DecalVariations.Read( cache, decal ).Count;
		var definitions = DecalPrefabs.LoadDefinitions( Host, tag.Path, count );
		if ( definitions.Count == 0 )
			return;

		var component = target.AddComponent<Decal>();
		component.Decals = definitions;
		component.Transient = true;
		component.Scale = decal.RadiusMin > 0 && decal.RadiusMin < decal.RadiusMax ? new ParticleFloat( decal.RadiusMin / decal.RadiusMax, 1 ) : 1;
		component.Rotation = decal.NoRandomRotation ? 0 : new ParticleFloat( 0, 360 );
		component.Depth = Math.Clamp( 2 * decal.RadiusMax * HaloCeMount.WorldScale, 4, 64 );
		component.SortLayer = DecalPrefabs.SortLayer( decal );

		// A random blend between the decal's color bounds; both left at black means untinted.
		var lower = (Vector3)decal.ColorLower;
		var upper = (Vector3)decal.ColorUpper;
		if ( lower == Vector3.Zero && upper == Vector3.Zero )
			lower = upper = Vector3.One;
		component.ColorTint = new ParticleGradient
		{
			Type = ParticleGradient.ValueType.Range,
			Evaluation = ParticleGradient.EvaluationType.Particle,
			ConstantA = new Color( lower.x, lower.y, lower.z ),
			ConstantB = new Color( upper.x, upper.y, upper.z ),
		};

		// The color mix is Halo's intensity: random between its bounds, which the environment
		// shader fades every blend function toward the bare surface by.
		component.ColorMix = decal.IntensityMin < decal.IntensityMax ? new ParticleFloat( decal.IntensityMin, decal.IntensityMax ) : decal.IntensityMax;

		if ( decal.LifetimeMax <= 0 )
			return;

		// Halo gives a decal a random life between the lifetime bounds, at full strength until the
		// last few seconds (random between the decay bounds), then fading linearly to nothing as its
		// life ends. One decaying longer than it lives starts out faded. The component picks the
		// life and the fade independently, so the fade is relative to the average life, and the
		// fade and the intensity come from one random pick between their lower and upper bounds.
		component.LifeTime = new ParticleFloat( Math.Max( decal.LifetimeMin, 0.01f ), decal.LifetimeMax );
		var lifetime = (decal.LifetimeMin + decal.LifetimeMax) * 0.5f;
		if ( decal.DecayTimeMax <= 0 )
			return;

		component.ColorMix = new ParticleFloat
		{
			Type = ParticleFloat.ValueType.CurveRange,
			Evaluation = ParticleFloat.EvaluationType.Life,
			CurveA = Fade( decal.DecayTimeMin / lifetime, decal.IntensityMin ),
			CurveB = Fade( decal.DecayTimeMax / lifetime, decal.IntensityMax ),
		};
	}

	/// <summary>
	/// Strength over a decal's life when it has <paramref name="intensity"/> and fades for the last
	/// <paramref name="decay"/> of its life.
	/// </summary>
	static Curve Fade( float decay, float intensity )
	{
		if ( decay <= 0 )
			return intensity;

		return decay < 1
			? new Curve( LinearFrame( 0, intensity ), LinearFrame( 1 - decay, intensity ), LinearFrame( 1, 0 ) )
			: new Curve( LinearFrame( 0, intensity / decay ), LinearFrame( 1, 0 ) );
	}

	static Curve.Frame LinearFrame( float time, float value ) => new( time, value ) { Mode = Curve.HandleMode.Linear };

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}
