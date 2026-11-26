using System.Runtime.InteropServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A corner of a particle quad for halo1_particles.shader. All four corners of a particle carry
/// the same particle; the shader places it and builds the sprite around it.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct ParticleVertex
{
	/// <summary>
	/// Weather: the particle's place in the repeating cube (0..1). Effects: the emitter's position.
	/// </summary>
	[VertexLayout.Position] public Vector3 Position;

	/// <summary>
	/// Corner (-1..1), and two random numbers (0..1). Effects use the first for the starting frame.
	/// </summary>
	[VertexLayout.TexCoord( 0 )] public Vector4 Corner;

	/// <summary>
	/// Color in gamma space, and alpha.
	/// </summary>
	[VertexLayout.TexCoord( 1 )] public Vector4 Color;

	/// <summary>
	/// Radius (s&amp;box units). Effects: lifetime, time offset into the cycle, cycle length (seconds).
	/// </summary>
	[VertexLayout.TexCoord( 2 )] public Vector4 Particle;

	/// <summary>
	/// Effects: initial velocity (s&amp;box units per second). Both: sprite rotation (radians).
	/// </summary>
	[VertexLayout.TexCoord( 3 )] public Vector4 Motion;

	public static readonly (float X, float Y)[] Corners = [(-1, 1), (1, 1), (1, -1), (-1, -1)];

	/// <summary>
	/// Adds the four corners of a particle, and its two triangles.
	/// </summary>
	public static void AddQuad( List<ParticleVertex> vertices, List<int> indices, ParticleVertex particle )
	{
		var start = vertices.Count;
		foreach ( var (x, y) in Corners )
		{
			var corner = particle;
			corner.Corner = new Vector4( x, y, particle.Corner.z, particle.Corner.w );
			vertices.Add( corner );
		}

		indices.AddRange( [start, start + 1, start + 2, start, start + 2, start + 3] );
	}
}

/// <summary>
/// Materials for halo1_particles.shader, shared by weather and effect particles.
/// </summary>
static class ParticleMaterial
{
	public const string ShaderPath = "shaders/halo1_particles.shader";

	/// <summary>
	/// The object functions a lightning material follows (g_vLightning&lt;source&gt; and its range).
	/// </summary>
	public static readonly string[] LightningSources = ["Jitter", "Thickness", "Brightness", "Tint"];

	/// <summary>
	/// A material drawing one sprite sequence of a bitmap, with every shader input set to a
	/// neutral value so the caller only sets what it uses.
	/// </summary>
	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry bitmap, int sequence, FramebufferBlendFunction blend, ParticleRenderMode orientation, bool weather )
	{
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		var (pages, frames) = Frames( cache, bitmap, sequence );
		var sprite = pages.Count > 1 ? PageAtlas( cache, bitmap, pages ) : MaterialTextures.Load( host, material, bitmap, pages[0] );
		material.Set( "g_tSprite", sprite ?? MaterialTextures.White );
		material.Set( "g_tFrames", FrameTexture( frames ) );
		material.Set( "g_flFrameCount", (float)frames.Count );
		material.Set( "g_flAnimationRate", 0f );
		material.Set( "g_flOrientation", (float)orientation );
		material.Set( "g_flFadeMode", 0f );

		material.Set( "g_vWeatherVelocity", Vector3.Zero );
		material.Set( "g_flWeatherCube", 1000f );
		material.Set( "g_flWander", 0f );
		material.Set( "g_vWeatherFade", new Vector4( 0, 0, 400, 500 ) );
		material.Set( "g_tWeatherMask", MaterialTextures.White );
		material.Set( "g_vMaskOrigin", Vector3.Zero );
		material.Set( "g_flMaskCell", 100f );
		material.Set( "g_vMaskSize", Vector3.One );
		material.Set( "g_flMaskColumns", 1f );
		material.Set( "g_flMaskValue", 0f );

		material.Set( "g_vGravity", Vector3.Zero );
		material.Set( "g_flDrag", 0f );
		material.Set( "g_vFadeTimes", Vector2.Zero );
		material.Set( "g_vRadiusAnimation", Vector2.One );
		material.Set( "g_tZMap", MaterialTextures.Transparent );
		material.Set( "g_flZSpriteScale", 0f );

		material.Set( "g_flLightning", 0f );
		foreach ( var source in LightningSources )
		{
			material.Set( $"g_vLightning{source}", new Vector4( 0, 1, 0, 1 ) );
			material.Set( $"g_vLightning{source}Range", new Vector2( 0, 1 ) );
		}
		material.Set( "g_vLightningTintLower", Vector3.One );
		material.Set( "g_vLightningTintUpper", Vector3.One );

		material.SetFeature( "F_WEATHER", weather ? 1 : 0 );
		material.SetFeature( "F_BLEND_MODE", (int)blend );
		material.SetFeature( "F_ZSPRITE", 0 );
		return material;
	}

	/// <summary>
	/// The sprites of a bitmap sequence as texture rectangles (left, top, right, bottom), and the bitmaps they're on: sprites on several bitmaps (c10's rain flips between two) are rectangles of the bitmaps stacked top to bottom (<see cref="PageAtlas"/>), or only the first bitmap's if they can't stack.
	/// </summary>
	static (List<int> Pages, List<Vector4> Frames) Frames( CacheFile cache, TagEntry bitmap, int sequence )
	{
		var frames = new List<Vector4>();
		if ( bitmap is null )
			return ([0], [new Vector4( 0, 0, 1, 1 )]);

		var tag = new BitmapTag( cache, bitmap );
		var seq = tag.Sequences.Count == 0 ? null : tag.Sequences[Math.Clamp( sequence, 0, tag.Sequences.Count - 1 )];
		if ( seq is null || seq.Sprites.Count == 0 )
			return ([Math.Max( seq?.FirstBitmapIndex ?? 0, 0 )], [new Vector4( 0, 0, 1, 1 )]);

		var pages = seq.Sprites.Select( s => s.BitmapIndex ).Distinct().ToList();
		if ( pages.Count > 1 && !CanStack( tag, pages ) )
			pages = [seq.Sprites[0].BitmapIndex];

		foreach ( var sprite in seq.Sprites )
		{
			var page = pages.IndexOf( sprite.BitmapIndex );
			if ( page >= 0 )
				frames.Add( new Vector4( sprite.Left, (page + sprite.Top) / pages.Count, sprite.Right, (page + sprite.Bottom) / pages.Count ) );
		}

		return (pages.Select( p => Math.Max( p, 0 ) ).ToList(), frames);
	}

	// Bitmaps stack into one texture if they're all decodable 2D images of one size, format and mip count.
	static bool CanStack( BitmapTag tag, List<int> pages )
	{
		if ( pages.Any( p => p < 0 || p >= tag.Bitmaps.Count ) )
			return false;

		var first = tag.Bitmaps[pages[0]];
		return pages.Select( p => tag.Bitmaps[p] ).All( b => b.Type == BitmapType.Texture2D && BitmapDecoder.CanDecodeImage( b.Format )
			&& b.Width == first.Width && b.Height == first.Height && b.Format == first.Format && b.LevelCount == first.LevelCount );
	}

	// The bitmaps stacked top to bottom in one texture with their own mips, so a particle can flip between sprites on different bitmaps as Halo's did.
	static Texture PageAtlas( CacheFile cache, TagEntry bitmap, List<int> pages )
	{
		var tag = new BitmapTag( cache, bitmap );
		var first = tag.Bitmaps[pages[0]];
		var pixels = pages.Select( p => BitmapTag.ReadPixels( cache, tag.Bitmaps[p] ) ).ToList();
		var levels = new List<byte[]>();
		var offset = 0;
		for ( var level = 0; level < first.LevelCount; level++ )
		{
			var width = Math.Max( 1, first.Width >> level );
			var height = Math.Max( 1, first.Height >> level );
			var size = first.GetLevelSize( level );
			// A level whose rows don't split evenly between the bitmaps would mix them.
			if ( Math.Max( 1, first.Height * pages.Count >> level ) != height * pages.Count || pixels.Any( p => p.Length < offset + size ) )
				break;

			var rgba = new byte[width * height * 4 * pages.Count];
			for ( var i = 0; i < pages.Count; i++ )
				BitmapDecoder.DecodeImage( first.Format, pixels[i].AsSpan( offset, size ), rgba.AsSpan( i * width * height * 4, width * height * 4 ), width, height );

			levels.Add( rgba );
			offset += size;
		}

		if ( levels.Count == 0 )
			return null;

		// s&box wants the smallest level first.
		var data = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Count - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( data, destination );
			destination += levels[level].Length;
		}

		return Texture.Create( first.Width, first.Height * pages.Count, ImageFormat.RGBA8888 )
			.WithData( data )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}

	static Texture FrameTexture( List<Vector4> frames )
	{
		var data = new float[frames.Count * 4];
		for ( var i = 0; i < frames.Count; i++ )
		{
			data[i * 4] = frames[i].x;
			data[i * 4 + 1] = frames[i].y;
			data[i * 4 + 2] = frames[i].z;
			data[i * 4 + 3] = frames[i].w;
		}

		return Texture.Create( frames.Count, 1, ImageFormat.RGBA32323232F )
			.WithData( new ReadOnlySpan<float>( data ) )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// Builds a mesh of particle quads.
	/// </summary>
	public static Mesh CreateMesh( Material material, List<ParticleVertex> vertices, List<int> indices, BBox bounds )
	{
		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		mesh.Bounds = bounds;
		return mesh;
	}
}
