using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// A Halo 2 sky (<c>sky </c>), as far as drawing its model needs it.
/// </summary>
/// <remarks>
/// The sky is an ordinary render model; its region names are numbers (layers) and every region is drawn.
/// Multiplayer skies seen: Lockout and Zanzibar are fixed in world space at scale 1 (their models are the
/// mountains and sea around the level, in world units), Coagulation is scaled 120, Ascension 50.
/// </remarks>
public sealed class SkyTag
{
	public static readonly TagClass Class = new( "sky " );

	public TagEntry Tag { get; }
	public TagEntry RenderModel { get; }

	/// <summary>
	/// 1 fixed in world space, 4 casts light from below, 0x20 use the clear colour...
	/// </summary>
	public int Flags { get; }

	/// <summary>
	/// The model's scale, as stored (0 = the default, which the tag's comment gives as 0.03).
	/// </summary>
	public float RenderModelScale { get; }

	/// <summary>
	/// How much the sky moves to stay centred on the player, as stored (0 = 1, no parallax).
	/// </summary>
	public float MovementScale { get; }

	public bool FixedInWorldSpace => (Flags & 1) != 0;

	/// <summary>
	/// Whether Halo clears the frame to <see cref="ClearColor"/> before drawing the sky (Zanzibar, Dune): what shows
	/// where the sky's model doesn't reach, down at the horizon and in the water's reflection of it.
	/// </summary>
	public bool UseClearColor => (Flags & 0x20) != 0;

	/// <summary>
	/// The clear colour (rgb 0-1, gamma space).
	/// </summary>
	public System.Numerics.Vector3 ClearColor { get; }

	/// <summary>
	/// The atmospheric fog (null for none): Halo 2 fogs everything but the sky by its distance from the camera.
	/// </summary>
	public Fog AtmosphericFog { get; }

	/// <summary>
	/// A second atmospheric fog added on top of the first (null for none; seldom used in multiplayer).
	/// </summary>
	public Fog SecondaryFog { get; }

	/// <summary>
	/// The sky fog (null for none): its colour and density over the sky itself, which the atmospheric fog leaves alone.
	/// </summary>
	public Fog SkyFog { get; }

	/// <summary>
	/// One of the sky's fog blocks: colour (rgb 0-1, gamma space), maximum density (0-1), and for the atmospheric ones
	/// the distances it starts and reaches its maximum density at (world units).
	/// </summary>
	public sealed record Fog( System.Numerics.Vector3 Color, float MaximumDensity, float StartDistance, float OpaqueDistance );

	/// <summary>
	/// The patchy fog seen in the sky's clusters (null for none).
	/// </summary>
	public PatchyFogBlock PatchyFog { get; }

	public SkyTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		RenderModel = cache.GetTagReference( tag.Address );
		Flags = data.I32( tag.Address + 0x10 );
		RenderModelScale = data.F32( tag.Address + 0x14 );
		MovementScale = data.F32( tag.Address + 0x18 );
		ClearColor = data.Vec3( tag.Address + 0xA0 );

		// Blocks of one element: atmospheric and secondary 0x18 bytes (colour, maximum density, start and opaque
		// distances), sky fog 0x10 (colour, density).
		foreach ( var a in data.Elements( tag.Address + 0x48, 0x18 ).Take( 1 ) )
			AtmosphericFog = new Fog( data.Vec3( a ), data.F32( a + 0xC ), data.F32( a + 0x10 ), data.F32( a + 0x14 ) );
		foreach ( var a in data.Elements( tag.Address + 0x50, 0x18 ).Take( 1 ) )
			SecondaryFog = new Fog( data.Vec3( a ), data.F32( a + 0xC ), data.F32( a + 0x10 ), data.F32( a + 0x14 ) );
		foreach ( var a in data.Elements( tag.Address + 0x58, 0x10 ).Take( 1 ) )
			SkyFog = new Fog( data.Vec3( a ), data.F32( a + 0xC ), 0, 0 );
		foreach ( var a in data.Elements( tag.Address + 0x60, 0x50 ).Take( 1 ) )
			PatchyFog = PatchyFogBlock.FromSky( cache, a );
	}
}
