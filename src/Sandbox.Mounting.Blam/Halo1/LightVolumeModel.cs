using System.Runtime.InteropServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds Halo's light volumes (<c>mgs2</c> widgets: the plasma bolts' glow, the glows on rockets,
/// needles and fuel rods) as models drawn by halo1_light_volume.shader, registered as
/// <c>mount://haloce/&lt;light volume tag&gt;__light_volume.vmdl</c>.
/// </summary>
/// <remarks>
/// The model's origin is the light volume's marker and its +X the marker's forward: the sprites
/// run along +X from the frame's offset over its length (the bolts' trail behind their origin).
/// The model is one quad at the marker; the shader puts it on screen around every sprite and adds
/// the sprites up per pixel the way Halo's frame buffer did (see the shader), from the frame's
/// numbers in the material. Halo picks the brightness scale function and the color interpolation
/// per frame; here the first frame is baked (Halo never gets past it) with the carrying object's
/// function value, in RGB.
/// </remarks>
static class LightVolumeModel
{
	public const string ShaderPath = "shaders/halo1_light_volume.shader";

	/// <summary>
	/// Most sprites the shader adds up (Halo's tags have at most 32).
	/// </summary>
	const int MaximumSprites = 64;

	public static string ResourceName( string tagPath ) => $"{HaloCeMount.ResourcePath( tagPath )}__light_volume";

	public static string FullPath( string tagPath ) => $"mount://{EffectPrefabs.Ident}/{ResourceName( tagPath )}.vmdl";

	/// <summary>
	/// A corner of the quad. The layout matches <see cref="HaloVertex"/>.
	/// </summary>
	[StructLayout( LayoutKind.Sequential )]
	struct LightVolumeVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.Tangent] public Vector4 Tangent;

		/// <summary>
		/// The corner, -1 to 1 on both axes.
		/// </summary>
		[VertexLayout.TexCoord( 0 )] public Vector2 Corner;

		[VertexLayout.TexCoord( 1 )] public Vector2 Unused;
	}

	/// <param name="brightness">The brightness scale function's value on the carrying object, 1 without one.</param>
	public static Model Build( HaloCeMount host, string name, CacheFile cache, TagEntry tag, float brightness )
	{
		var volume = new LightVolumeTag( cache, tag );
		var sprites = volume.Sprites();
		if ( sprites.Count == 0 || brightness <= 0 )
			return null;

		var material = Material.Create( $"{name}/light_volume.vmat", ShaderPath );
		if ( material is null )
			return null;

		// Without a map Halo uses the globals' glow (rasterizer\glow, a soft round spot).
		var scale = HaloCeMount.WorldScale;
		var frame = volume.Frames[0];
		var map = volume.Map ?? GlowBitmap( cache );
		material.Set( "g_tLightVolume", MaterialTextures.Load( host, material, map, BitmapIndex( cache, map, volume.SequenceIndex ) ) ?? MaterialTextures.White );
		material.Set( "g_vLightVolumeBrightness", new Vector3( volume.PerpendicularBrightnessScale, volume.ParallelBrightnessScale, MathF.Min( brightness, 1 ) ) );
		material.Set( "g_vLightVolumeFade", new Vector2( volume.NearFadeDistance, volume.FarFadeDistance ) * scale );
		material.Set( "g_vLightVolumeOffset", new Vector4( frame.OffsetFromMarker * scale, frame.OffsetExponent, frame.Length * scale, Math.Min( volume.Count, MaximumSprites ) ) );
		material.Set( "g_vLightVolumeRadius", new Vector3( frame.RadiusHither * scale, frame.RadiusYon * scale, frame.RadiusExponent ) );
		material.Set( "g_vLightVolumeColorHither", new Vector4( frame.ColorHither.X, frame.ColorHither.Y, frame.ColorHither.Z, frame.ColorHither.W ) );
		material.Set( "g_vLightVolumeColorYon", new Vector4( frame.ColorYon.X, frame.ColorYon.Y, frame.ColorYon.Z, frame.ColorYon.W ) );
		material.Set( "g_vLightVolumeExponents", new Vector2( frame.ColorExponent, frame.BrightnessExponent ) );

		// Rendering bounds: every sprite's square.
		var bounds = new BBox( Vector3.Zero, Vector3.Zero );
		foreach ( var (offset, radius, _) in sprites )
			bounds = bounds.AddBBox( BBox.FromPositionAndSize( new Vector3( offset * scale, 0, 0 ), radius * scale * 2 ) );

		var vertices = new List<LightVolumeVertex>( 4 );
		foreach ( var (x, y) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) } )
			vertices.Add( new LightVolumeVertex { Normal = Vector3.Forward, Tangent = new Vector4( 0, 1, 0, 1 ), Corner = new Vector2( x, y ) } );

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( 6, new List<int> { 0, 1, 2, 0, 2, 3 } );
		mesh.Bounds = bounds;

		return Model.Builder.WithName( name ).AddMesh( mesh ).Create();
	}

	/// <summary>
	/// The globals' glow bitmap (rasterizer data 0x60), which light volumes without a map use.
	/// </summary>
	static TagEntry GlowBitmap( CacheFile cache )
	{
		var globals = cache.Tags.FirstOrDefault( t => t.Class.ToString() == "matg" );
		if ( globals is null )
			return null;

		var entry = cache.TagData.Block( globals.Address + 0x134, 0x1AC ).FirstOrDefault();
		return entry == 0 ? null : cache.GetTagReference( cache.TagData, entry + 0x60 );
	}

	/// <summary>
	/// The bitmap Halo draws for a sequence index: the sequence's first bitmap, or the bitmap of that
	/// index in tags without sequences.
	/// </summary>
	static int BitmapIndex( CacheFile cache, TagEntry map, int sequence )
	{
		if ( map is null )
			return 0;

		var bitmap = new BitmapTag( cache, map );
		if ( sequence >= 0 && sequence < bitmap.Sequences.Count && bitmap.Sequences[sequence].BitmapCount > 0 )
			return Math.Clamp( bitmap.Sequences[sequence].FirstBitmapIndex, 0, Math.Max( bitmap.Bitmaps.Count - 1, 0 ) );

		return Math.Clamp( sequence, 0, Math.Max( bitmap.Bitmaps.Count - 1, 0 ) );
	}

	/// <summary>
	/// The value Halo scales the light volume's brightness by on <paramref name="carrier"/>: its
	/// brightness scale source (an A-D out function), or 1 without one. A function that only runs on
	/// time (the plasma bolts' constant A) gives its steady value; one driven by the object's inputs
	/// (the marines' flashlight beam on the assault rifle) is taken as on, 1, since it's only drawn
	/// while it's on: Halo skips a light volume whose value is 0, and so should the game.
	/// </summary>
	public static float Brightness( CacheFile cache, TagEntry lightVolume, TagEntry carrier )
	{
		var source = new LightVolumeTag( cache, lightVolume ).BrightnessScaleSource;
		if ( source is < 1 or > 4 || carrier is null )
			return 1;

		var functions = ObjectFunctions.Read( cache, carrier );
		if ( source > functions.Count || !functions[source - 1].IsFreeRunning )
			return 1;

		return ObjectFunctions.RestValues( cache, carrier )[source - 1];
	}
}

/// <summary>
/// Loads a light volume's model (see <see cref="LightVolumeModel"/>), with the function values of
/// the first object found carrying it.
/// </summary>
sealed class LightVolumeModelLoader( string mapPath, uint tagId, uint carrierId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( tag is null )
			return null;

		return LightVolumeModel.Build( Host, Path, cache, tag, LightVolumeModel.Brightness( cache, tag, cache.GetTag( carrierId ) ) );
	}
}
